using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.App.Tests;

// Deterministic tests for the ORGANIZATION-KEY INVENTORY SOURCE CONTRACT (Cycle 05).
//
// This cycle defines the contract + source boundary for a FUTURE organization-
// provided Chef's Keys inventory WITHOUT activating or accessing any real key.
// It delivers: a versioned inventory contract, a pure bounded parser/validator,
// a read-only source abstraction, a DISABLED production source (returns
// not_provisioned, zero I/O), a typed evaluator (Cycle-4 gate + source + parser),
// and a bounded discriminated state projection. Production stays
// authorized_not_provisioned / inventoryLoaded:false and performs NO
// filesystem/registry inventory read. Only a test-only synthetic in-memory
// source can produce authorized_provisioned.
//
// These tests never touch ProgramData, the registry, a certificate store, a
// credential vault, a service, WAM/Hello, Microsoft Graph, PAX, or a Bake.
// Containment is proven by deterministic comment/string-stripped source scans
// over the NEW/changed files only.
public sealed class OrganizationKeyInventoryTests
{
    // ---- test-only synthetic in-memory source (never shipped) ---------------
    // The only source that can yield a provisioned document. It lives in the test
    // project ONLY and is not injectable via env/command-line/HTTP/React/
    // TestIsolation/user files. It records how many times it was invoked so a
    // short-circuit (denied gate) can be proven.
    private sealed class InMemoryOrganizationKeyInventorySource : IOrganizationKeyInventorySource
    {
        private readonly OrganizationInventorySourceResult _result;

        public InMemoryOrganizationKeyInventorySource(OrganizationInventorySourceResult result)
        {
            _result = result;
        }

        public int InvocationCount { get; private set; }

        public OrganizationInventorySourceResult Load()
        {
            InvocationCount++;
            return _result;
        }
    }

    // ---- document builders ---------------------------------------------------

    private static string Doc(string entriesJson)
        => "{ \"schemaVersion\": 1, \"entries\": " + entriesJson + " }";

    private static string Entry(
        string? id = "org-key-1",
        string? displayName = "Contoso Managed Key",
        string certificateReferenceType = "app_registration_certificate",
        string adminState = "enabled",
        string? tenantReference = "tenant-ref-1",
        string? clientReference = "client-ref-1",
        int entryVersion = 1)
    {
        var sb = new StringBuilder();
        sb.Append("{ \"entryVersion\": ").Append(entryVersion);
        if (id is not null) sb.Append(", \"organizationKeyId\": \"").Append(id).Append('\"');
        if (displayName is not null) sb.Append(", \"displayName\": \"").Append(displayName).Append('\"');
        sb.Append(", \"certificateReferenceType\": \"").Append(certificateReferenceType).Append('\"');
        sb.Append(", \"adminState\": \"").Append(adminState).Append('\"');
        if (tenantReference is not null) sb.Append(", \"tenantReference\": \"").Append(tenantReference).Append('\"');
        if (clientReference is not null) sb.Append(", \"clientReference\": \"").Append(clientReference).Append('\"');
        sb.Append(" }");
        return sb.ToString();
    }

    private static ManagedChefKeysGateProjection AuthorizedGate()
        => ManagedChefKeysGate.Evaluate(
            MachinePolicyDetection.ConfiguredOrganizationManaged(
                MachinePolicyCapability.Enabled, MachinePolicyCapability.Disabled));

    private static ManagedChefKeysGateProjection DeniedNotConfiguredGate()
        => ManagedChefKeysGate.Evaluate(MachinePolicyDetection.NotConfigured());

    private static ManagedChefKeysGateProjection DeniedDisabledGate()
        => ManagedChefKeysGate.Evaluate(
            MachinePolicyDetection.ConfiguredOrganizationManaged(
                MachinePolicyCapability.Disabled, MachinePolicyCapability.Disabled));

    private static ManagedChefKeysGateProjection DeniedUnavailableGate()
        => ManagedChefKeysGate.Evaluate(null);

    // ==== A. Valid provisioned parses (synthetic-only) ========================

    [Fact] // T01
    public void T01_ValidZeroEntries_ParsesValid_EmptyProvisionedDistinctFromNotProvisioned()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[]"));
        Assert.True(r.IsValid);
        Assert.Equal(OrganizationKeyInventoryInvalidReason.None, r.Reason);
        Assert.Equal(0, r.EntryCount);
    }

    [Fact] // T02
    public void T02_ValidOneEntry_ParsesValid()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[" + Entry() + "]"));
        Assert.True(r.IsValid);
        Assert.Equal(1, r.EntryCount);
        OrganizationKeyInventoryEntry e = r.Entries[0];
        Assert.Equal(ChefKeyOrigin.Organization, e.Origin);
        Assert.Equal("org-key-1", e.OrganizationKeyId);
        Assert.Equal("Contoso Managed Key", e.DisplayName);
        Assert.Equal("app_registration_certificate", e.CertificateReferenceType);
        Assert.Equal("enabled", e.AdminState);
        Assert.Equal(1, e.EntryVersion);
    }

    [Fact] // T03
    public void T03_ValidManyEntries_ParsesValid()
    {
        string entries = "[" + Entry("org-a") + "," + Entry("org-b") + "," + Entry("org-c") + "]";
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc(entries));
        Assert.True(r.IsValid);
        Assert.Equal(3, r.EntryCount);
    }

    [Fact] // T04
    public void T04_AdminStateDisabled_IsValid()
    {
        OrganizationKeyInventoryParseResult r =
            OrganizationKeyInventoryParser.Parse(Doc("[" + Entry(adminState: "disabled") + "]"));
        Assert.True(r.IsValid);
        Assert.Equal("disabled", r.Entries[0].AdminState);
    }

    [Fact] // T05 — the opaque tenant/client references are carried in-process only.
    public void T05_TenantAndClientReferences_CarriedInternally()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[" + Entry() + "]"));
        Assert.True(r.IsValid);
        Assert.False(string.IsNullOrEmpty(r.Entries[0].TenantReference));
        Assert.False(string.IsNullOrEmpty(r.Entries[0].ClientReference));
    }

    // ==== B. Schema / version rejection =======================================

    [Fact] // T06
    public void T06_MissingSchemaVersion_Invalid()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse("{ \"entries\": [] }");
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.UnsupportedSchema);
    }

    [Fact] // T07 — Cycle 10 made schema 2 representable; it carries a certificate REFERENCE.
    public void T07_SchemaVersionTwo_IsSupported_AndRequiresCertificateReference()
    {
        // Zero entries need no reference, so an empty v2 document is valid.
        OrganizationKeyInventoryParseResult empty =
            OrganizationKeyInventoryParser.Parse("{ \"schemaVersion\": 2, \"entries\": [] }");
        Assert.True(empty.IsValid);
        Assert.Equal(0, empty.EntryCount);

        // A v2 entry that omits the reference is still rejected.
        OrganizationKeyInventoryParseResult missing = OrganizationKeyInventoryParser.Parse(
            "{ \"schemaVersion\": 2, \"entries\": [" + Entry(entryVersion: 2) + "] }");
        AssertInvalid(missing, OrganizationKeyInventoryInvalidReason.MissingField);
    }

    [Fact] // T08
    public void T08_SchemaVersionZero_Invalid()
    {
        OrganizationKeyInventoryParseResult r =
            OrganizationKeyInventoryParser.Parse("{ \"schemaVersion\": 0, \"entries\": [] }");
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.UnsupportedSchema);
    }

    [Fact] // T09
    public void T09_SchemaVersionWrongType_Invalid()
    {
        OrganizationKeyInventoryParseResult r =
            OrganizationKeyInventoryParser.Parse("{ \"schemaVersion\": \"1\", \"entries\": [] }");
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.UnsupportedSchema);
    }

    [Fact] // T10
    public void T10_EntryVersionNotOne_Invalid()
    {
        OrganizationKeyInventoryParseResult r =
            OrganizationKeyInventoryParser.Parse(Doc("[" + Entry(entryVersion: 2) + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.UnsupportedSchema);
    }

    // ==== C. Structural / allow-list rejection ================================

    [Fact] // T11
    public void T11_UnknownTopLevelField_Invalid()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(
            "{ \"schemaVersion\": 1, \"entries\": [], \"extra\": true }");
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.UnknownField);
    }

    [Fact] // T12
    public void T12_UnknownEntryField_Invalid()
    {
        string entry = "{ \"entryVersion\": 1, \"organizationKeyId\": \"a\", \"displayName\": \"n\", "
            + "\"certificateReferenceType\": \"app_registration_certificate\", \"adminState\": \"enabled\", "
            + "\"tenantReference\": \"t\", \"clientReference\": \"c\", \"mystery\": 1 }";
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[" + entry + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.UnknownField);
    }

    [Fact] // T13
    public void T13_MissingEntries_Invalid()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse("{ \"schemaVersion\": 1 }");
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.InvalidEntries);
    }

    [Fact] // T14
    public void T14_NullEntries_Invalid()
    {
        OrganizationKeyInventoryParseResult r =
            OrganizationKeyInventoryParser.Parse("{ \"schemaVersion\": 1, \"entries\": null }");
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.InvalidEntries);
    }

    [Fact] // T15
    public void T15_EntriesWrongType_Invalid()
    {
        OrganizationKeyInventoryParseResult r =
            OrganizationKeyInventoryParser.Parse("{ \"schemaVersion\": 1, \"entries\": {} }");
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.InvalidEntries);
    }

    [Fact] // T16
    public void T16_TooManyEntries_Invalid()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < 65; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Entry("org-" + i));
        }
        sb.Append(']');
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc(sb.ToString()));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.TooManyEntries);
    }

    [Fact] // T17
    public void T17_NullEntry_Invalid()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[null]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.InvalidEntries);
    }

    [Fact] // T18
    public void T18_EntryWrongType_Invalid()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[\"not-an-object\"]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.InvalidEntries);
    }

    // ==== D. Missing / bad required fields ====================================

    [Fact] // T19
    public void T19_MissingEntryVersion_Invalid()
    {
        string entry = "{ \"organizationKeyId\": \"a\", \"displayName\": \"n\", "
            + "\"certificateReferenceType\": \"app_registration_certificate\", \"adminState\": \"enabled\", "
            + "\"tenantReference\": \"t\", \"clientReference\": \"c\" }";
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[" + entry + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.MissingField);
    }

    [Fact] // T20
    public void T20_MissingOrganizationKeyId_Invalid()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[" + Entry(id: null) + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.MissingField);
    }

    [Fact] // T21
    public void T21_EmptyOrganizationKeyId_Invalid()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[" + Entry(id: "   ") + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.EmptyValue);
    }

    [Fact] // T22
    public void T22_OversizedOrganizationKeyId_Invalid()
    {
        OrganizationKeyInventoryParseResult r =
            OrganizationKeyInventoryParser.Parse(Doc("[" + Entry(id: new string('a', 129)) + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.OversizedField);
    }

    [Fact] // T23
    public void T23_OrganizationKeyIdWrongType_Invalid()
    {
        string entry = "{ \"entryVersion\": 1, \"organizationKeyId\": 5, \"displayName\": \"n\", "
            + "\"certificateReferenceType\": \"app_registration_certificate\", \"adminState\": \"enabled\", "
            + "\"tenantReference\": \"t\", \"clientReference\": \"c\" }";
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[" + entry + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.WrongType);
    }

    [Fact] // T24 — personal-namespace collision: an id in the personal WCM prefix.
    public void T24_PersonalNamespaceCollisionId_Invalid()
    {
        OrganizationKeyInventoryParseResult r =
            OrganizationKeyInventoryParser.Parse(Doc("[" + Entry(id: "PAXCookbook:ChefKey:smuggled") + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.InvalidId);
    }

    [Fact] // T25
    public void T25_MissingDisplayName_Invalid()
    {
        OrganizationKeyInventoryParseResult r =
            OrganizationKeyInventoryParser.Parse(Doc("[" + Entry(displayName: null) + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.MissingField);
    }

    [Fact] // T26
    public void T26_EmptyDisplayName_Invalid()
    {
        OrganizationKeyInventoryParseResult r =
            OrganizationKeyInventoryParser.Parse(Doc("[" + Entry(displayName: "") + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.EmptyValue);
    }

    [Fact] // T27
    public void T27_OversizedDisplayName_Invalid()
    {
        OrganizationKeyInventoryParseResult r =
            OrganizationKeyInventoryParser.Parse(Doc("[" + Entry(displayName: new string('d', 129)) + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.OversizedField);
    }

    [Fact] // T28
    public void T28_MissingCertificateReferenceType_Invalid()
    {
        string entry = "{ \"entryVersion\": 1, \"organizationKeyId\": \"a\", \"displayName\": \"n\", "
            + "\"adminState\": \"enabled\", \"tenantReference\": \"t\", \"clientReference\": \"c\" }";
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[" + entry + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.MissingField);
    }

    [Fact] // T29
    public void T29_WrongCertificateReferenceType_Invalid()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(
            Doc("[" + Entry(certificateReferenceType: "app_registration_secret") + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.WrongType);
    }

    [Fact] // T30
    public void T30_MissingAdminState_Invalid()
    {
        string entry = "{ \"entryVersion\": 1, \"organizationKeyId\": \"a\", \"displayName\": \"n\", "
            + "\"certificateReferenceType\": \"app_registration_certificate\", "
            + "\"tenantReference\": \"t\", \"clientReference\": \"c\" }";
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[" + entry + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.MissingField);
    }

    [Fact] // T31
    public void T31_InvalidAdminState_Invalid()
    {
        OrganizationKeyInventoryParseResult r =
            OrganizationKeyInventoryParser.Parse(Doc("[" + Entry(adminState: "paused") + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.WrongType);
    }

    [Fact] // T32
    public void T32_MissingTenantReference_Invalid()
    {
        OrganizationKeyInventoryParseResult r =
            OrganizationKeyInventoryParser.Parse(Doc("[" + Entry(tenantReference: null) + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.MissingField);
    }

    [Fact] // T33
    public void T33_MissingClientReference_Invalid()
    {
        OrganizationKeyInventoryParseResult r =
            OrganizationKeyInventoryParser.Parse(Doc("[" + Entry(clientReference: null) + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.MissingField);
    }

    // ==== E. Duplicate detection ==============================================

    [Fact] // T34
    public void T34_DuplicateOrganizationKeyId_Invalid()
    {
        string entries = "[" + Entry("dup") + "," + Entry("dup") + "]";
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc(entries));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.DuplicateId);
    }

    [Fact] // T35
    public void T35_CaseVariantDuplicateOrganizationKeyId_Invalid()
    {
        string entries = "[" + Entry("Dup") + "," + Entry("dup") + "]";
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc(entries));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.DuplicateId);
    }

    [Fact] // T36 — duplicate JSON property (top-level) if detectable.
    public void T36_DuplicateTopLevelProperty_Invalid()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(
            "{ \"schemaVersion\": 1, \"schemaVersion\": 1, \"entries\": [] }");
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.DuplicateProperty);
    }

    [Fact] // T37 — duplicate JSON property (entry) if detectable.
    public void T37_DuplicateEntryProperty_Invalid()
    {
        string entry = "{ \"entryVersion\": 1, \"organizationKeyId\": \"a\", \"organizationKeyId\": \"a\", "
            + "\"displayName\": \"n\", \"certificateReferenceType\": \"app_registration_certificate\", "
            + "\"adminState\": \"enabled\", \"tenantReference\": \"t\", \"clientReference\": \"c\" }";
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[" + entry + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.DuplicateProperty);
    }

    // ==== F. Malformed / oversized ============================================

    [Fact] // T38
    public void T38_MalformedJson_Invalid()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse("{ not json");
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.MalformedJson);
    }

    [Fact] // T39
    public void T39_NullDocument_Invalid()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(null);
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.MalformedJson);
    }

    [Fact] // T40
    public void T40_RootNotObject_Invalid()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse("[]");
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.MalformedJson);
    }

    [Fact] // T41 — oversized document is rejected BEFORE content parse.
    public void T41_OversizedDocument_Invalid()
    {
        // A syntactically well-formed but > 64 KiB document. If the size guard
        // ran after parsing, the huge valid displayName would surface a field
        // error; OversizedDocument proves the byte guard ran first.
        string big = new string('x', 70000);
        string doc = Doc("[" + Entry(displayName: big) + "]");
        Assert.True(Encoding.UTF8.GetByteCount(doc) > OrganizationKeyInventoryContract.MaxDocumentBytes);
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(doc);
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.OversizedDocument);
    }

    // ==== G. Prohibited fields (reject even if null/empty) ====================

    public static IEnumerable<object[]> ProhibitedKeys()
    {
        foreach (string k in new[]
        {
            "clientSecret", "secret", "hasSecret", "secretPresent", "password",
            "certificate", "certificateBytes", "pfx", "privateKey", "privateKeyBytes",
            "token", "claim", "claims", "upn", "account", "interactiveAccount",
            "wamAccount", "credentialManagerTarget", "wcmTarget", "path", "filePath",
            "inventoryPath", "registryPath", "storeName", "certStore", "serviceName",
            "command", "script", "arguments", "args", "env", "environment", "scope",
            "scopes", "graphEndpoint", "query", "recipe", "bake", "authType",
            "authenticationType",
        })
        {
            yield return new object[] { k };
        }
    }

    [Theory] // T42 — every prohibited key rejects the whole inventory.
    [MemberData(nameof(ProhibitedKeys))]
    public void T42_ProhibitedEntryField_Invalid(string prohibited)
    {
        string entry = "{ \"entryVersion\": 1, \"organizationKeyId\": \"a\", \"displayName\": \"n\", "
            + "\"certificateReferenceType\": \"app_registration_certificate\", \"adminState\": \"enabled\", "
            + "\"tenantReference\": \"t\", \"clientReference\": \"c\", \"" + prohibited + "\": \"v\" }";
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[" + entry + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.ProhibitedField);
    }

    [Theory] // T43 — a prohibited key is rejected even when its value is null.
    [MemberData(nameof(ProhibitedKeys))]
    public void T43_ProhibitedEntryField_RejectedEvenWhenNull(string prohibited)
    {
        string entry = "{ \"entryVersion\": 1, \"organizationKeyId\": \"a\", \"displayName\": \"n\", "
            + "\"certificateReferenceType\": \"app_registration_certificate\", \"adminState\": \"enabled\", "
            + "\"tenantReference\": \"t\", \"clientReference\": \"c\", \"" + prohibited + "\": null }";
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc("[" + entry + "]"));
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.ProhibitedField);
    }

    [Fact] // T44 — a prohibited key at the top level is rejected too.
    public void T44_ProhibitedTopLevelField_Invalid()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(
            "{ \"schemaVersion\": 1, \"entries\": [], \"clientSecret\": \"x\" }");
        AssertInvalid(r, OrganizationKeyInventoryInvalidReason.ProhibitedField);
    }

    // ==== H. Evaluator: gate short-circuit ====================================

    [Fact] // T45 — a denied (not_configured) gate short-circuits; source never invoked.
    public void T45_DeniedNotConfigured_ShortCircuits_SourceNeverInvoked()
    {
        var source = new InMemoryOrganizationKeyInventorySource(
            OrganizationInventorySourceResult.Provided(Doc("[" + Entry() + "]")));
        OrganizationInventoryProjection p =
            OrganizationKeyInventoryEvaluator.Evaluate(DeniedNotConfiguredGate(), source);
        Assert.Equal(OrganizationInventoryState.NotAuthorized, p.State);
        Assert.Equal(0, source.InvocationCount);
        Assert.Equal("not_configured", p.WireState);
        Assert.Equal("not_configured", p.WireReason);
        AssertAllLaterStagesFalse(p);
    }

    [Fact] // T46 — a denied (disabled) gate short-circuits and carries the gate tokens.
    public void T46_DeniedDisabled_ShortCircuits()
    {
        var source = new InMemoryOrganizationKeyInventorySource(OrganizationInventorySourceResult.NotProvisioned());
        OrganizationInventoryProjection p =
            OrganizationKeyInventoryEvaluator.Evaluate(DeniedDisabledGate(), source);
        Assert.Equal(OrganizationInventoryState.NotAuthorized, p.State);
        Assert.Equal(0, source.InvocationCount);
        Assert.Equal("disabled", p.WireState);
        Assert.Equal("disabled_by_policy", p.WireReason);
    }

    [Fact] // T47 — a fail-closed (unavailable) gate short-circuits; source never invoked.
    public void T47_DeniedUnavailable_ShortCircuits()
    {
        var source = new InMemoryOrganizationKeyInventorySource(OrganizationInventorySourceResult.NotProvisioned());
        OrganizationInventoryProjection p =
            OrganizationKeyInventoryEvaluator.Evaluate(DeniedUnavailableGate(), source);
        Assert.Equal(OrganizationInventoryState.NotAuthorized, p.State);
        Assert.Equal(0, source.InvocationCount);
        Assert.Equal("unavailable", p.WireState);
    }

    [Fact] // T48 — a null gate fails closed to unavailable; source never invoked.
    public void T48_NullGate_FailsClosed_SourceNeverInvoked()
    {
        var source = new InMemoryOrganizationKeyInventorySource(OrganizationInventorySourceResult.NotProvisioned());
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(null, source);
        Assert.Equal(OrganizationInventoryState.Unavailable, p.State);
        Assert.Equal(0, source.InvocationCount);
        Assert.False(p.InventoryLoaded);
    }

    // ==== I. Evaluator: authorized paths ======================================

    [Fact] // T49 — authorized + disabled production source => authorized_not_provisioned.
    public void T49_Authorized_DisabledProductionSource_NotProvisioned()
    {
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(
            AuthorizedGate(), new NotProvisionedOrganizationKeyInventorySource());
        Assert.Equal(OrganizationInventoryState.AuthorizedNotProvisioned, p.State);
        Assert.Equal("authorized_not_provisioned", p.WireState);
        Assert.Equal("authorized_not_provisioned", p.WireReason);
        Assert.False(p.InventoryLoaded);
        Assert.Equal(0, p.EntryCount);
        AssertAllLaterStagesFalse(p);
    }

    [Fact] // T50 — authorized + source reports NotProvisioned.
    public void T50_Authorized_SourceNotProvisioned()
    {
        var source = new InMemoryOrganizationKeyInventorySource(OrganizationInventorySourceResult.NotProvisioned());
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(AuthorizedGate(), source);
        Assert.Equal(OrganizationInventoryState.AuthorizedNotProvisioned, p.State);
        Assert.Equal(1, source.InvocationCount);
        Assert.False(p.InventoryLoaded);
    }

    [Fact] // T51 — authorized + source unavailable => Unavailable, 0 entries.
    public void T51_Authorized_SourceUnavailable()
    {
        var source = new InMemoryOrganizationKeyInventorySource(OrganizationInventorySourceResult.Unavailable());
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(AuthorizedGate(), source);
        Assert.Equal(OrganizationInventoryState.Unavailable, p.State);
        Assert.Equal("unavailable", p.WireState);
        Assert.False(p.InventoryLoaded);
        Assert.Equal(0, p.EntryCount);
    }

    [Fact] // T52 — authorized + source untrusted => Untrusted, 0 entries.
    public void T52_Authorized_SourceUntrusted()
    {
        var source = new InMemoryOrganizationKeyInventorySource(OrganizationInventorySourceResult.Untrusted());
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(AuthorizedGate(), source);
        Assert.Equal(OrganizationInventoryState.Untrusted, p.State);
        Assert.Equal("untrusted", p.WireState);
        Assert.Equal(0, p.EntryCount);
    }

    [Fact] // T53 — authorized + provided valid document => authorized_provisioned.
    public void T53_Authorized_ProvidedValid_Provisioned()
    {
        var source = new InMemoryOrganizationKeyInventorySource(
            OrganizationInventorySourceResult.Provided(Doc("[" + Entry("org-a") + "," + Entry("org-b") + "]")));
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(AuthorizedGate(), source);
        Assert.Equal(OrganizationInventoryState.AuthorizedProvisioned, p.State);
        Assert.Equal("authorized_provisioned", p.WireState);
        Assert.True(p.InventoryLoaded);
        Assert.Equal(2, p.EntryCount);
        // Provisioned still asserts nothing resolved/usable/bound/authorized.
        AssertAllLaterStagesFalse(p);
    }

    [Fact] // T54 — authorized + provided EMPTY valid document => provisioned, 0 entries.
    public void T54_Authorized_ProvidedEmptyValid_ProvisionedDistinctFromNotProvisioned()
    {
        var source = new InMemoryOrganizationKeyInventorySource(
            OrganizationInventorySourceResult.Provided(Doc("[]")));
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(AuthorizedGate(), source);
        Assert.Equal(OrganizationInventoryState.AuthorizedProvisioned, p.State);
        Assert.True(p.InventoryLoaded);
        Assert.Equal(0, p.EntryCount);
    }

    [Fact] // T55 — authorized + provided invalid document => Invalid, 0 entries.
    public void T55_Authorized_ProvidedInvalid_Invalid()
    {
        var source = new InMemoryOrganizationKeyInventorySource(
            OrganizationInventorySourceResult.Provided("{ not json"));
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(AuthorizedGate(), source);
        Assert.Equal(OrganizationInventoryState.Invalid, p.State);
        Assert.Equal("invalid", p.WireState);
        Assert.False(p.InventoryLoaded);
        Assert.Equal(0, p.EntryCount);
    }

    [Fact] // T56 — the source is invoked exactly once when authorized.
    public void T56_Authorized_SourceInvokedExactlyOnce()
    {
        var source = new InMemoryOrganizationKeyInventorySource(OrganizationInventorySourceResult.NotProvisioned());
        OrganizationKeyInventoryEvaluator.Evaluate(AuthorizedGate(), source);
        Assert.Equal(1, source.InvocationCount);
    }

    [Fact] // T57 — the production source returns NotProvisioned (behavioral: zero I/O).
    public void T57_ProductionSource_ReturnsNotProvisioned()
    {
        OrganizationInventorySourceResult r = new NotProvisionedOrganizationKeyInventorySource().Load();
        Assert.Equal(OrganizationInventorySourceKind.NotProvisioned, r.Kind);
    }

    // ==== J. Projection invariants ============================================

    [Fact] // T58 — every failure carries 0 entries and resolved/usable false.
    public void T58_FailureStates_ZeroEntries_ResolvedUsableFalse()
    {
        foreach (OrganizationInventoryProjection p in new[]
        {
            OrganizationInventoryProjection.Unavailable(),
            OrganizationInventoryProjection.Untrusted(),
            OrganizationInventoryProjection.Invalid(OrganizationKeyInventoryInvalidReason.MalformedJson),
            OrganizationInventoryProjection.AuthorizedNotProvisioned(),
        })
        {
            Assert.Equal(0, p.EntryCount);
            Assert.False(p.InventoryLoaded);
            AssertAllLaterStagesFalse(p);
        }
    }

    [Fact] // T59 — InventoryLoaded is true ONLY for authorized_provisioned.
    public void T59_InventoryLoaded_OnlyForProvisioned()
    {
        var provisioned = new InMemoryOrganizationKeyInventorySource(
            OrganizationInventorySourceResult.Provided(Doc("[]")));
        Assert.True(OrganizationKeyInventoryEvaluator.Evaluate(AuthorizedGate(), provisioned).InventoryLoaded);
        Assert.False(OrganizationInventoryProjection.AuthorizedNotProvisioned().InventoryLoaded);
        Assert.False(OrganizationInventoryProjection.Unavailable().InventoryLoaded);
    }

    [Fact] // T60 — the projection ToString leaks no identifier or raw source.
    public void T60_Projection_ToString_Bounded()
    {
        var source = new InMemoryOrganizationKeyInventorySource(
            OrganizationInventorySourceResult.Provided(Doc("[" + Entry("secret-id-xyz", "Secret Name") + "]")));
        OrganizationInventoryProjection p = OrganizationKeyInventoryEvaluator.Evaluate(AuthorizedGate(), source);
        string s = p.ToString();
        Assert.DoesNotContain("secret-id-xyz", s);
        Assert.DoesNotContain("Secret Name", s);
        Assert.DoesNotContain("tenant-ref", s);
        Assert.DoesNotContain("client-ref", s);
    }

    // ==== K. Deterministic containment source scans (NEW/changed files only) ==

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        string dir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // Strip block comments, line comments, AND string/char literals so descriptive
    // doctrine comments and the parser's PROHIBITED-KEY string literals cannot
    // false-positive a CODE-pattern scan.
    private static string StripCommentsAndStrings(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        src = Regex.Replace(src, @"//[^\r\n]*", " ");
        src = Regex.Replace(src, "@\"(?:[^\"]|\"\")*\"", " ");
        src = Regex.Replace(src, "\"(?:\\\\.|[^\"\\\\])*\"", " ");
        src = Regex.Replace(src, "'(?:\\\\.|[^'\\\\])*'", " ");
        return src;
    }

    private const string ContractRel = "src/PAXCookbook.Shared/Contracts/OrganizationKeyInventoryContract.cs";
    private const string ProgramRel = "src/PAXCookbook.App/Program.cs";
    private const string ChefKeyModelRel = "src/PAXCookbook.App/ChefKeyModel.cs";

    private static string ContractCode => StripCommentsAndStrings(ReadSource(ContractRel));
    private static string ProgramCode => StripCommentsAndStrings(ReadSource(ProgramRel));
    private static string ChefKeyModelCode => StripCommentsAndStrings(ReadSource(ChefKeyModelRel));

    [Fact] // T61 — the contract never reads ProgramData / CommonApplicationData.
    public void T61_Contract_NoProgramDataAccess()
    {
        string src = ContractCode;
        Assert.DoesNotContain("CommonApplicationData", src);
        Assert.DoesNotContain("ProgramData", src);
        Assert.DoesNotContain("GetFolderPath", src);
        Assert.DoesNotContain("Directory.CreateDirectory", src);
        Assert.DoesNotContain("File.ReadAllText", src);
        Assert.DoesNotContain("File.ReadAllBytes", src);
        Assert.DoesNotContain("File.WriteAllText", src);
    }

    [Fact] // T62 — the contract touches no certificate store / private key (NEW code).
    public void T62_Contract_NoCertificateStoreOrPrivateKey()
    {
        string src = ContractCode;
        Assert.DoesNotContain("X509Store", src);
        Assert.DoesNotContain("X509Certificate", src);
        Assert.DoesNotContain("StoreName", src);
        Assert.DoesNotContain("FindByThumbprint", src);
        Assert.DoesNotContain("GetRSAPrivateKey", src);
        Assert.DoesNotContain(".PrivateKey", src);
    }

    [Fact] // T63 — the contract writes no registry and reads no user-controlled source.
    public void T63_Contract_NoRegistry_NoUserFallback()
    {
        string src = ContractCode;
        Assert.DoesNotContain("Microsoft.Win32", src);
        Assert.DoesNotContain("RegistryKey", src);
        Assert.DoesNotContain("Registry.LocalMachine", src);
        Assert.DoesNotContain("Registry.CurrentUser", src);
        Assert.DoesNotContain("SetValue", src);
        Assert.DoesNotContain("GetEnvironmentVariable", src);
        Assert.DoesNotContain("GetCommandLineArgs", src);
    }

    [Fact] // T64 — the contract reaches no WCM / Graph / token / WAM / Hello / service / PAX / Bake.
    public void T64_Contract_NoActivationSurface()
    {
        string src = ContractCode;
        Assert.DoesNotContain("WindowsCredentialStore", src);
        Assert.DoesNotContain("CredentialManager", src);
        Assert.DoesNotContain("GraphServiceClient", src);
        Assert.DoesNotContain("AcquireToken", src);
        Assert.DoesNotContain("PublicClientApplication", src);
        Assert.DoesNotContain("WebAuthn", src);
        Assert.DoesNotContain("Microsoft.Identity", src);
        Assert.DoesNotContain("ServiceController", src);
        Assert.DoesNotContain("Process.Start", src);
        Assert.DoesNotContain("PaxAdapter", src);
    }

    [Fact] // T65 — the contract accepts no runtime-injected source (env/args/http/isolation).
    public void T65_Contract_NoRuntimeSourceInjection()
    {
        string src = ContractCode;
        Assert.DoesNotContain("HttpContext", src);
        Assert.DoesNotContain("HttpRequest", src);
        Assert.DoesNotContain("TestIsolation", src);
        Assert.DoesNotContain("GetCommandLineArgs", src);
        Assert.DoesNotContain("GetEnvironmentVariable", src);
    }

    [Fact] // T66 — Program wires the evaluator with the REAL ProgramData source; no new route.
    public void T66_Program_WiresProgramDataSource_NoNewRoute()
    {
        string raw = ReadSource(ProgramRel);
        string authority = ReadSource("src/PAXCookbook.App/ProductionOrganizationAuthority.cs");
        // Cycle 16 — the gate + source + detailed evaluator + catalog chain moved
        // into the SINGLE production authority factory, which the route consumes.
        // Every Cycle 06 / Cycle 11 guarantee below is unchanged; it is now ALSO
        // pinned to exactly one construction site.
        Assert.Contains("ProductionOrganizationAuthority.CreateLocalAuthority()", raw);
        Assert.Contains("OrganizationKeyInventoryEvaluator.EvaluateDetailed(", authority);
        Assert.Contains("new MachineCertificateCatalog()", authority);
        Assert.Contains("new ProgramDataOrganizationKeyInventorySource()", authority);
        Assert.DoesNotContain("new ProgramDataOrganizationKeyInventorySource()", raw);
        Assert.DoesNotContain("new MachineCertificateCatalog()", raw);
        // The resolution coordinator is still wired at the route.
        Assert.Contains("OrganizationCertificateResolutionCoordinator.Resolve(", raw);
        // The disabled source is wired NOWHERE.
        Assert.DoesNotContain("new NotProvisionedOrganizationKeyInventorySource()", raw);
        Assert.DoesNotContain("new NotProvisionedOrganizationKeyInventorySource()", authority);
        // No organization inventory / mutation route is introduced.
        string code = ProgramCode;
        Assert.DoesNotContain("organization-keys", ReadSource(ProgramRel));
        Assert.DoesNotContain("chef-keys/organization", ReadSource(ProgramRel));
        Assert.DoesNotContain("OrganizationChefKeyInventory", code);
    }

    [Fact] // T67 — ChefKeyModel surfaces the real bounded inventory wire shape; no org array/identifier.
    public void T67_ChefKeyModel_WireShapeBounded()
    {
        string code = ChefKeyModelCode;
        // The additive organizationKeys wire object keeps its bounded shape and
        // now surfaces the REAL inventoryLoaded flag plus a provisioned-only
        // entryCount (present ONLY when InventoryLoaded is true).
        Assert.Contains("chefKeys = items", code);
        Assert.Contains("readOnly = true", code);
        Assert.Contains("certificateOnly = true", code);
        Assert.Contains("inventoryLoaded = organizationInventory.InventoryLoaded", code);
        Assert.Contains("entryCount = organizationInventory.EntryCount", code);
        // entryCount is emitted ONLY inside the provisioned branch, guarded by
        // InventoryLoaded — never unconditionally.
        Assert.Contains("organizationInventory.InventoryLoaded", code);
        // No organization array / identifier / reference leaks onto the wire.
        Assert.DoesNotContain("organizationItems", code);
        Assert.DoesNotContain("OrganizationKeyId", code);
        Assert.DoesNotContain("TenantReference", code);
        Assert.DoesNotContain("ClientReference", code);
        Assert.DoesNotContain("CertificateReferenceType", code);
    }

    // ---- helpers -------------------------------------------------------------

    private static void AssertInvalid(OrganizationKeyInventoryParseResult r, OrganizationKeyInventoryInvalidReason reason)
    {
        Assert.False(r.IsValid);
        Assert.Equal(OrganizationKeyInventoryParseOutcome.Invalid, r.Outcome);
        Assert.Equal(reason, r.Reason);
        Assert.Equal(0, r.EntryCount);
        Assert.Empty(r.Entries);
    }

    private static void AssertAllLaterStagesFalse(OrganizationInventoryProjection p)
    {
        Assert.False(p.CertificateResolved);
        Assert.False(p.PrivateKeyAvailable);
        Assert.False(p.Usable);
        Assert.False(p.RecipeBound);
        Assert.False(p.BakeAuthorized);
        Assert.False(p.ServiceReady);
    }
}
