using System;
using System.Collections.Generic;
using System.Text.Json;
using PAXCookbook.App;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle 14 — ORGANIZATION-KEY RECIPE BINDING.
//
// Binding and bounded readiness ONLY. Every input here is SYNTHETIC or INJECTED:
// nothing opens, enumerates, reads, installs, or mutates a real certificate
// store, touches a private key, reads a secret, injects a Cook credential,
// contacts a tenant/Graph/service/WAM, runs PAX, or performs a Bake. Inventory
// documents are in-memory strings and catalog occurrences are hand-built bounded
// records.
public sealed class OrganizationKeyRecipeBindingTests
{
    // ---- recipe tree builders (mirror the request pipeline's JSON -> tree) ----

    private static object? ToTree(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Object => BuildObject(el),
        JsonValueKind.Array => BuildArray(el),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out long l) ? (object)l : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static Dictionary<string, object?> BuildObject(JsonElement el)
    {
        var o = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (JsonProperty p in el.EnumerateObject()) { o[p.Name] = ToTree(p.Value); }
        return o;
    }

    private static List<object?> BuildArray(JsonElement el)
    {
        var a = new List<object?>();
        foreach (JsonElement i in el.EnumerateArray()) { a.Add(ToTree(i)); }
        return a;
    }

    private static Dictionary<string, object?> Recipe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return BuildObject(doc.RootElement);
    }

    private const string ValidUlid = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string OrgKeyId = "contoso.managed_key-1";

    private static string Fmt(List<object> errors) => JsonSerializer.Serialize(errors);

    private static bool HasKeyword(List<object> errors, string keyword)
    {
        foreach (object e in errors)
        {
            if (e is Dictionary<string, object?> d &&
                d.TryGetValue("keyword", out object? k) &&
                string.Equals(k as string, keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static Dictionary<string, object?> RecipeWithAuth(string authJson) => Recipe($$"""
    {
      "recipeId": "{{ValidUlid}}",
      "recipeSchemaVersion": 1,
      "paxAdapterVersion": "1.11.11",
      "identity": { "name": "Organization binding" },
      "ingredients": {
        "m365Usage": { "includeM365Usage": false },
        "entraUserData": { "includeUserInfo": false }
      },
      "query": { "mode": "audit", "dateMode": "previous-day" },
      "processing": {},
      "destinations": { "fact": { "mode": "outputPath", "path": "C:\\PAX\\audit.csv" } },
      "auth": {{authJson}}
    }
    """);

    private static Dictionary<string, object?> OrganizationBoundRecipe(string id = OrgKeyId)
        => RecipeWithAuth($$"""
        {
          "mode": "AppRegistrationCertificate",
          "tenantId": "{{TenantId}}",
          "organizationKeyId": "{{id}}"
        }
        """);

    // ---- synthetic inventory + catalog seams ---------------------------------

    private sealed class InMemorySource : IOrganizationKeyInventorySource
    {
        private readonly OrganizationInventorySourceResult _result;

        public InMemorySource(OrganizationInventorySourceResult result) { _result = result; }

        public OrganizationInventorySourceResult Load() => _result;
    }

    // Fails the test if it is ever consulted, so a missing short-circuit cannot
    // pass silently.
    private sealed class NeverQueriedCatalog : ICertificateCatalog
    {
        public int QueryCount { get; private set; }

        public CertificateCatalogResult Query()
        {
            QueryCount++;
            throw new InvalidOperationException("The catalog must not be consulted in this state.");
        }
    }

    private static ManagedChefKeysGateProjection AuthorizedGate()
        => ManagedChefKeysGate.Evaluate(
            MachinePolicyDetection.ConfiguredOrganizationManaged(
                MachinePolicyCapability.Enabled, MachinePolicyCapability.Disabled));

    private static ManagedChefKeysGateProjection DeniedGate()
        => ManagedChefKeysGate.Evaluate(MachinePolicyDetection.NotConfigured());

    private const string FingerprintA =
        "A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1";
    private const string FingerprintB =
        "B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2";

    private static string Common(string id, string adminState)
        => ", \"organizationKeyId\": \"" + id + "\""
           + ", \"displayName\": \"Contoso Managed Key\""
           + ", \"certificateReferenceType\": \"app_registration_certificate\""
           + ", \"adminState\": \"" + adminState + "\""
           + ", \"tenantReference\": \"tenant-ref-1\""
           + ", \"clientReference\": \"client-ref-1\"";

    private static string V2Entry(string id, string fingerprint, string adminState = "enabled")
        => "{ \"entryVersion\": 2" + Common(id, adminState)
           + ", \"certificateSha256\": \"" + fingerprint.ToLowerInvariant() + "\" }";

    private static string V1Entry(string id, string adminState = "enabled")
        => "{ \"entryVersion\": 1" + Common(id, adminState) + " }";

    private static string Doc(int schemaVersion, params string[] entries)
        => "{ \"schemaVersion\": " + schemaVersion + ", \"entries\": [" + string.Join(",", entries) + "] }";

    private static OrganizationInventoryEvaluation Provisioned(string document)
        => OrganizationKeyInventoryEvaluator.EvaluateDetailed(
            AuthorizedGate(), new InMemorySource(OrganizationInventorySourceResult.Provided(document)));

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static ICertificateUsabilityClock Clock() => new FixedCertificateUsabilityClock(Now);

    private const string ClientAuthOid =
        OrganizationCertificateUsabilityContract.ClientAuthenticationPurposeOid;

    private static CertificateUsabilityFacts UsableFacts() => CertificateUsabilityFacts.Create(
        Now.AddDays(-1), Now.AddDays(30),
        CertificateKeyAlgorithmClass.Rsa, CertificateKeyAvailability.Available,
        purposeExtensionPresent: true, purposeOids: new[] { ClientAuthOid },
        keyUsageExtensionPresent: true, digitalSignatureAllowed: true);

    private static ICertificateCatalog CatalogWith(
        params (string Fingerprint, CertificateUsabilityFacts? Facts)[] occurrences)
    {
        var list = new List<CertificateCatalogOccurrence>();
        for (int i = 0; i < occurrences.Length; i++)
        {
            list.Add(CertificateCatalogOccurrence.Create(
                occurrences[i].Fingerprint, i, occurrences[i].Facts));
        }
        return new SyntheticCertificateCatalog(CertificateCatalogResult.Available(list));
    }

    private static OrganizationKeyBindingReadiness? Project(
        Dictionary<string, object?> recipe,
        OrganizationInventoryEvaluation? inventory,
        ICertificateCatalog? catalog,
        ICertificateUsabilityClock? clock)
        => RecipeReadinessModel.ProjectOrganizationKeyBinding(recipe, inventory, catalog, clock);

    private static OrganizationKeyBindingReadiness Require(OrganizationKeyBindingReadiness? readiness)
    {
        Assert.NotNull(readiness);
        return readiness!;
    }

    // ==== A. Save-time schema and binding gates ==============================

    [Fact] // T01 — an existing Recipe with NO binding at all is still valid.
    public void T01_RecipeWithoutBinding_StillValid()
    {
        var recipe = RecipeWithAuth("""{ "mode": "WebLogin" }""");

        (bool ok, List<object> errors) = RecipeValidationModel.ValidateAll(recipe);

        Assert.True(ok, "an unbound recipe must remain valid: " + Fmt(errors));
        Assert.Null(Project(recipe, Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))), null, Clock()));
    }

    [Fact] // T02 — an existing PERSONAL binding is untouched and still valid.
    public void T02_PersonalBinding_StillValid()
    {
        var recipe = RecipeWithAuth($$"""
        {
          "mode": "AppRegistrationCertificate",
          "tenantId": "{{TenantId}}",
          "chefKeyId": "personal-key-1"
        }
        """);

        (bool ok, List<object> errors) = RecipeValidationModel.ValidateAll(recipe);

        Assert.True(ok, "a personally bound recipe must remain valid: " + Fmt(errors));
        // No organization binding was named, so there is nothing to project.
        Assert.Null(Project(recipe, Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))), null, Clock()));
    }

    [Fact] // T03 — an organization binding validates and ROUND-TRIPS as the opaque id ALONE.
    public void T03_OrganizationBinding_RoundTrips()
    {
        var recipe = OrganizationBoundRecipe();

        (bool ok, List<object> errors) = RecipeValidationModel.ValidateAll(recipe);
        Assert.True(ok, "an organization-bound cert recipe must validate: " + Fmt(errors));

        // Persistence round-trip: serialize, re-parse, revalidate.
        var reparsed = Recipe(JsonSerializer.Serialize(recipe));
        (bool ok2, List<object> errors2) = RecipeValidationModel.ValidateAll(reparsed);
        Assert.True(ok2, "the round-tripped recipe must still validate: " + Fmt(errors2));

        var auth = (Dictionary<string, object?>)reparsed["auth"]!;
        Assert.Equal(OrgKeyId, auth["organizationKeyId"]);
        // The binding carries the opaque id AND NOTHING ELSE: no certificate
        // reference, fingerprint, thumbprint, display name, store, or key metadata.
        Assert.Equal(
            new[] { "mode", "organizationKeyId", "tenantId" },
            new SortedSet<string>(auth.Keys, StringComparer.Ordinal));
    }

    [Fact] // T04 — mutual exclusion, and by the BINDING gate, not by additionalProperties.
    public void T04_BothBindingTypes_Rejected()
    {
        var recipe = RecipeWithAuth($$"""
        {
          "mode": "AppRegistrationCertificate",
          "tenantId": "{{TenantId}}",
          "chefKeyId": "personal-key-1",
          "organizationKeyId": "org.key_1"
        }
        """);

        (bool ok, List<object> errors) = RecipeValidationModel.ValidateAll(recipe);

        Assert.False(ok, "a recipe carrying both bindings must be rejected");
        Assert.True(
            HasKeyword(errors, "organizationKeyBindingConflict"),
            "rejection must come from the mutual-exclusion binding gate: " + Fmt(errors));
        Assert.False(
            HasKeyword(errors, "additionalProperties"),
            "organizationKeyId must be a known optional property, not an unknown field: " + Fmt(errors));
    }

    [Theory] // T05 — a malformed identifier is refused, including a ':' separator.
    [InlineData("has:colon")]
    [InlineData("wcm:target:contoso")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("")]
    public void T05_InvalidOrganizationId_Rejected(string id)
    {
        var recipe = OrganizationBoundRecipe(id);

        (bool ok, List<object> errors) = RecipeValidationModel.ValidateAll(recipe);

        Assert.False(ok, "a malformed organization id must be rejected: " + id);
        Assert.True(HasKeyword(errors, "pattern"), "rejection must be the schema pattern: " + Fmt(errors));
    }

    [Theory] // T06 — organization origin is certificate-only; every other mode refuses.
    [InlineData("WebLogin")]
    [InlineData("DeviceCode")]
    [InlineData("AppRegistrationSecret")]
    [InlineData("ManagedIdentity")]
    public void T06_WrongAuthMode_Rejected(string mode)
    {
        var recipe = RecipeWithAuth($$"""
        {
          "mode": "{{mode}}",
          "tenantId": "{{TenantId}}",
          "organizationKeyId": "{{OrgKeyId}}"
        }
        """);

        (bool ok, List<object> errors) = RecipeValidationModel.ValidateAll(recipe);

        Assert.False(ok, "auth.mode '" + mode + "' must refuse an organization binding");
        Assert.True(
            HasKeyword(errors, "organizationKeyAuthModeUnsupported"),
            "rejection must come from the auth-mode gate: " + Fmt(errors));
    }

    // ==== B. Bounded, fail-closed readiness ==================================

    [Fact] // T07 — authorized + provisioned + enabled + usable => ready.
    public void T07_Ready()
    {
        OrganizationKeyBindingReadiness r = Require(Project(
            OrganizationBoundRecipe(),
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            CatalogWith((FingerprintA, UsableFacts())),
            Clock()));

        Assert.Equal(OrganizationKeyBindingState.OrganizationKeyReady, r.State);
        Assert.Equal("organization_key_ready", r.WireState);
        Assert.True(r.Ready);
    }

    [Fact] // T08 — policy denies organization keys; the catalog is never consulted.
    public void T08_PolicyDenied()
    {
        var catalog = new NeverQueriedCatalog();
        OrganizationInventoryEvaluation evaluation = OrganizationKeyInventoryEvaluator.EvaluateDetailed(
            DeniedGate(),
            new InMemorySource(OrganizationInventorySourceResult.Provided(
                Doc(2, V2Entry(OrgKeyId, FingerprintA)))));

        OrganizationKeyBindingReadiness r = Require(
            Project(OrganizationBoundRecipe(), evaluation, catalog, Clock()));

        Assert.Equal(OrganizationKeyBindingState.OrganizationKeyNotAuthorized, r.State);
        Assert.Equal("organization_key_not_authorized", r.WireState);
        Assert.False(r.Ready);
        Assert.Equal(0, catalog.QueryCount);
    }

    [Theory] // T09 — every non-provisioned inventory state fails closed together.
    [InlineData("not_provisioned")]
    [InlineData("unavailable")]
    [InlineData("untrusted")]
    [InlineData("invalid")]
    public void T09_InventoryNotProvisioned(string kind)
    {
        OrganizationInventorySourceResult source = kind switch
        {
            "not_provisioned" => OrganizationInventorySourceResult.NotProvisioned(),
            "unavailable" => OrganizationInventorySourceResult.Unavailable(),
            "untrusted" => OrganizationInventorySourceResult.Untrusted(),
            _ => OrganizationInventorySourceResult.Provided("{ not json"),
        };

        var catalog = new NeverQueriedCatalog();
        OrganizationInventoryEvaluation evaluation = OrganizationKeyInventoryEvaluator.EvaluateDetailed(
            AuthorizedGate(), new InMemorySource(source));

        OrganizationKeyBindingReadiness r = Require(
            Project(OrganizationBoundRecipe(), evaluation, catalog, Clock()));

        Assert.Equal(OrganizationKeyBindingState.OrganizationInventoryNotProvisioned, r.State);
        Assert.Equal("organization_inventory_not_provisioned", r.WireState);
        Assert.False(r.Ready);
        Assert.Equal(0, catalog.QueryCount);
    }

    [Fact] // T10 — the bound id is not in the trusted inventory; the catalog is never consulted.
    public void T10_OrganizationKeyNotFound()
    {
        var catalog = new NeverQueriedCatalog();

        OrganizationKeyBindingReadiness r = Require(Project(
            OrganizationBoundRecipe("some.other_key"),
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            catalog,
            Clock()));

        Assert.Equal(OrganizationKeyBindingState.OrganizationKeyNotFound, r.State);
        Assert.Equal("organization_key_not_found", r.WireState);
        Assert.False(r.Ready);
        Assert.Equal(0, catalog.QueryCount);
    }

    [Fact] // T11 — duplicate ids are impossible by construction AND still fail closed.
    public void T11_DuplicateId_ImpossibleAndFailsClosed()
    {
        // (a) The Cycle-5 parser rejects a duplicate id ordinal-ignore-case, so a
        //     duplicate can never become a provisioned inventory at all.
        OrganizationInventoryEvaluation parsed = Provisioned(
            Doc(2, V2Entry(OrgKeyId, FingerprintA), V2Entry(OrgKeyId.ToUpperInvariant(), FingerprintB)));
        Assert.Equal(OrganizationInventoryState.Invalid, parsed.Projection.State);
        Assert.Equal(
            OrganizationKeyBindingState.OrganizationInventoryNotProvisioned,
            Require(Project(OrganizationBoundRecipe(), parsed, new NeverQueriedCatalog(), Clock())).State);

        // (b) Even if a duplicate were somehow handed to the evaluator, it fails
        //     closed rather than silently picking one.
        var catalog = new NeverQueriedCatalog();
        OrganizationInventoryEvaluation forged = OrganizationInventoryEvaluation.Provisioned(
            OrganizationInventoryProjection.AuthorizedProvisioned(2),
            new[]
            {
                OrganizationKeyInventoryEntry.Create(
                    2, OrgKeyId, "A", OrganizationKeyInventoryContract.CertificateReferenceType,
                    OrganizationKeyInventoryContract.AdminStateEnabled, "t", "c", FingerprintA),
                OrganizationKeyInventoryEntry.Create(
                    2, OrgKeyId, "B", OrganizationKeyInventoryContract.CertificateReferenceType,
                    OrganizationKeyInventoryContract.AdminStateEnabled, "t", "c", FingerprintB),
            });

        OrganizationKeyBindingReadiness r = Require(
            Project(OrganizationBoundRecipe(), forged, catalog, Clock()));

        Assert.Equal(OrganizationKeyBindingState.Unknown, r.State);
        Assert.Equal("unknown", r.WireState);
        Assert.False(r.Ready);
        Assert.Equal(0, catalog.QueryCount);
    }

    [Fact] // T12 — an administrator-disabled entry refuses before the catalog opens.
    public void T12_EntryDisabled()
    {
        var catalog = new NeverQueriedCatalog();

        OrganizationKeyBindingReadiness r = Require(Project(
            OrganizationBoundRecipe(),
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA, adminState: "disabled"))),
            catalog,
            Clock()));

        Assert.Equal(OrganizationKeyBindingState.OrganizationKeyDisabled, r.State);
        Assert.Equal("organization_key_disabled", r.WireState);
        Assert.False(r.Ready);
        Assert.Equal(0, catalog.QueryCount);
    }

    [Fact] // T13 — a schema-v1 entry carries no reference; the catalog is never opened.
    public void T13_CertificateReferenceMissing()
    {
        var catalog = new NeverQueriedCatalog();

        OrganizationKeyBindingReadiness r = Require(Project(
            OrganizationBoundRecipe(),
            Provisioned(Doc(1, V1Entry(OrgKeyId))),
            catalog,
            Clock()));

        Assert.Equal(OrganizationKeyBindingState.CertificateReferenceMissing, r.State);
        Assert.Equal("certificate_reference_missing", r.WireState);
        Assert.False(r.Ready);
        Assert.Equal(0, catalog.QueryCount);
    }

    [Fact] // T14 — the catalog answered but reported no occurrence.
    public void T14_CertificateNotFound()
    {
        OrganizationKeyBindingReadiness r = Require(Project(
            OrganizationBoundRecipe(),
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            CatalogWith((FingerprintB, UsableFacts())),
            Clock()));

        Assert.Equal(OrganizationKeyBindingState.CertificateNotFound, r.State);
        Assert.Equal("certificate_not_found", r.WireState);
        Assert.False(r.Ready);
    }

    [Fact] // T15 — more than one occurrence is ambiguous, so fail closed.
    public void T15_CertificateAmbiguous()
    {
        OrganizationKeyBindingReadiness r = Require(Project(
            OrganizationBoundRecipe(),
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            CatalogWith((FingerprintA, UsableFacts()), (FingerprintA, UsableFacts())),
            Clock()));

        Assert.Equal(OrganizationKeyBindingState.CertificateAmbiguous, r.State);
        Assert.Equal("certificate_ambiguous", r.WireState);
        Assert.False(r.Ready);
    }

    [Theory] // T16 — no catalog answered: null, unavailable, or not connected (production).
    [InlineData("null")]
    [InlineData("unavailable")]
    [InlineData("not_connected")]
    public void T16_CatalogUnavailable(string kind)
    {
        ICertificateCatalog? catalog = kind switch
        {
            "null" => null,
            "unavailable" => new SyntheticCertificateCatalog(CertificateCatalogResult.Unavailable()),
            _ => new DisabledProductionCertificateCatalog(),
        };

        OrganizationKeyBindingReadiness r = Require(Project(
            OrganizationBoundRecipe(),
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            catalog,
            Clock()));

        Assert.Equal(OrganizationKeyBindingState.CatalogUnavailable, r.State);
        Assert.Equal("catalog_unavailable", r.WireState);
        Assert.False(r.Ready);
    }

    [Theory] // T17 — a unique match whose bounded usability refuses, per category.
    [InlineData("not_yet_valid")]
    [InlineData("expired")]
    [InlineData("client_auth_not_allowed")]
    [InlineData("digital_signature_not_allowed")]
    [InlineData("unsupported_key_algorithm")]
    [InlineData("private_key_unavailable")]
    [InlineData("extraction_invalid")]
    [InlineData("facts_missing")]
    [InlineData("clock_missing")]
    public void T17_CertificateUnusable_EachCategory(string category)
    {
        CertificateUsabilityFacts? facts = category switch
        {
            "not_yet_valid" => CertificateUsabilityFacts.Create(
                Now.AddDays(10), Now.AddDays(30),
                CertificateKeyAlgorithmClass.Rsa, CertificateKeyAvailability.Available,
                purposeExtensionPresent: true, purposeOids: new[] { ClientAuthOid },
                keyUsageExtensionPresent: true, digitalSignatureAllowed: true),
            "expired" => CertificateUsabilityFacts.Create(
                Now.AddDays(-30), Now.AddDays(-1),
                CertificateKeyAlgorithmClass.Rsa, CertificateKeyAvailability.Available,
                purposeExtensionPresent: true, purposeOids: new[] { ClientAuthOid },
                keyUsageExtensionPresent: true, digitalSignatureAllowed: true),
            "client_auth_not_allowed" => CertificateUsabilityFacts.Create(
                Now.AddDays(-1), Now.AddDays(30),
                CertificateKeyAlgorithmClass.Rsa, CertificateKeyAvailability.Available,
                purposeExtensionPresent: false,
                keyUsageExtensionPresent: true, digitalSignatureAllowed: true),
            "digital_signature_not_allowed" => CertificateUsabilityFacts.Create(
                Now.AddDays(-1), Now.AddDays(30),
                CertificateKeyAlgorithmClass.Rsa, CertificateKeyAvailability.Available,
                purposeExtensionPresent: true, purposeOids: new[] { ClientAuthOid },
                keyUsageExtensionPresent: false),
            "unsupported_key_algorithm" => CertificateUsabilityFacts.Create(
                Now.AddDays(-1), Now.AddDays(30),
                CertificateKeyAlgorithmClass.Unsupported, CertificateKeyAvailability.Available,
                purposeExtensionPresent: true, purposeOids: new[] { ClientAuthOid },
                keyUsageExtensionPresent: true, digitalSignatureAllowed: true),
            "private_key_unavailable" => CertificateUsabilityFacts.Create(
                Now.AddDays(-1), Now.AddDays(30),
                CertificateKeyAlgorithmClass.Rsa, CertificateKeyAvailability.Unavailable,
                purposeExtensionPresent: true, purposeOids: new[] { ClientAuthOid },
                keyUsageExtensionPresent: true, digitalSignatureAllowed: true),
            "extraction_invalid" => CertificateUsabilityFacts.ExtractionUnavailable(),
            "clock_missing" => UsableFacts(),
            _ => null,
        };

        ICertificateUsabilityClock? clock = category == "clock_missing" ? null : Clock();

        OrganizationKeyBindingReadiness r = Require(Project(
            OrganizationBoundRecipe(),
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            CatalogWith((FingerprintA, facts)),
            clock));

        Assert.Equal(OrganizationKeyBindingState.CertificateUnusable, r.State);
        Assert.Equal("certificate_unusable", r.WireState);
        Assert.False(r.Ready);
    }

    [Fact] // T18 — even READY is never Bake-authorized.
    public void T18_Ready_IsNeverBakeAuthorized()
    {
        OrganizationKeyBindingReadiness r = Require(Project(
            OrganizationBoundRecipe(),
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            CatalogWith((FingerprintA, UsableFacts())),
            Clock()));

        Assert.True(r.Ready);
        Assert.False(r.BakeAuthorized);
        Assert.False(r.ExecutionTested);
    }

    [Fact] // T19 — even READY is never service-ready or authentication-verified.
    public void T19_Ready_IsNeverServiceReady()
    {
        OrganizationKeyBindingReadiness r = Require(Project(
            OrganizationBoundRecipe(),
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            CatalogWith((FingerprintA, UsableFacts())),
            Clock()));

        Assert.True(r.Ready);
        Assert.False(r.ServiceReady);
        Assert.False(r.AuthenticationVerified);
        Assert.True(r.ReadOnly);
        Assert.True(r.ReferenceOnly);
        Assert.True(r.KeyNeverUsed);
        // The readiness object itself is content-free.
        Assert.DoesNotContain(OrgKeyId, r.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OrgKeyId, r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ==== C. Non-leakage and unchanged surfaces ==============================

    // NOTE ON SCOPE: recipe import/export and scrub are React-side surfaces and
    // React is deliberately untouched this cycle, so T20/T21 assert the
    // equivalent BACKEND invariants: the persistence round trip carries the
    // opaque id and nothing else, and the only backend auth-node scrub preserves
    // it while removing no other protection.

    [Fact] // T20 — a persistence round trip preserves ONLY the opaque id.
    public void T20_RoundTrip_PreservesOnlyTheOpaqueId()
    {
        var recipe = OrganizationBoundRecipe();
        var reparsed = Recipe(JsonSerializer.Serialize(recipe));

        Assert.True(RecipeValidationModel.ValidateAll(reparsed).Ok);

        string json = JsonSerializer.Serialize(reparsed);
        Assert.Contains("\"organizationKeyId\":\"" + OrgKeyId + "\"", json, StringComparison.Ordinal);
        // No organization-side metadata may ever ride along with the reference.
        foreach (string banned in new[]
                 {
                     "displayName", "tenantReference", "clientReference",
                     "certificateSha256", "certificateReferenceType", "thumbprint", "adminState",
                 })
        {
            Assert.DoesNotContain(banned, json, StringComparison.OrdinalIgnoreCase);
        }

        // The closed schema keeps it that way: any smuggled companion field is a
        // hard save failure.
        var smuggled = OrganizationBoundRecipe();
        ((Dictionary<string, object?>)smuggled["auth"]!)["certificateSha256"] = FingerprintA;
        Assert.False(RecipeValidationModel.ValidateAll(smuggled).Ok);
    }

    [Fact] // T21 — the backend auth scrub preserves the opaque id and removes no other protection.
    public void T21_Scrub_PreservesOpaqueId_AndRemovesNoProtection()
    {
        var recipe = OrganizationBoundRecipe();
        var auth = (Dictionary<string, object?>)recipe["auth"]!;
        auth["authProfileId"] = "legacy-profile-1";

        RecipeValidationModel.StripDeprecatedAuthFields(recipe);

        Assert.False(auth.ContainsKey("authProfileId"));
        Assert.Equal(OrgKeyId, auth["organizationKeyId"]);
        Assert.Equal(TenantId, auth["tenantId"]);
        Assert.Equal("AppRegistrationCertificate", auth["mode"]);
        Assert.True(RecipeValidationModel.ValidateAll(recipe).Ok);

        // Every other protection still fires on an organization-bound recipe.
        var withSecret = OrganizationBoundRecipe();
        withSecret["advanced"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["extraArguments"] = "-ClientSecret abc",
        };
        (bool ok, List<object> errors) = RecipeValidationModel.ValidateAll(withSecret);
        Assert.False(ok);
        Assert.True(HasKeyword(errors, "secretShape"), Fmt(errors));

        var oneLake = OrganizationBoundRecipe();
        ((Dictionary<string, object?>)((Dictionary<string, object?>)oneLake["destinations"]!)["fact"]!)["path"] =
            "abfss://x@y.dfs.core.windows.net/z.csv";
        Assert.True(HasKeyword(RecipeValidationModel.ValidateAll(oneLake).Errors, "m1OutputTier"));
    }

    [Fact] // T22 — the organization id never reaches the command preview or PAX argv.
    public void T22_CommandPreview_EmitsNoOrganizationId()
    {
        var recipe = OrganizationBoundRecipe();
        Assert.True(RecipeValidationModel.ValidateAll(recipe).Ok);

        // A synthetic PERSONAL key row is supplied only so the projection runs to
        // completion; the organization binding contributes nothing either way.
        var chefKey = new PaxAdapter.ChefKeyAuthRow(
            "AppRegistrationCertificate", "00000000-0000-0000-0000-000000000001", "ABCDEF0123456789");

        PaxAdapter.InvocationPlan plan = PaxAdapter.GetInvocationPlan(
            recipe, "C:\\PAX\\pax.ps1", chefKey, "local-manual");

        Assert.DoesNotContain(OrgKeyId, plan.PaxCommand, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OrgKeyId, plan.SpawnCommand, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organizationKeyId", plan.PaxCommand, StringComparison.OrdinalIgnoreCase);
        foreach (string token in plan.PaxArgv)
        {
            Assert.DoesNotContain(OrgKeyId, token, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact] // T23 — Cook argv is byte-identical and no organization credential is injected.
    public void T23_CookArgv_Unchanged_NoOrganizationCredential()
    {
        var chefKey = new PaxAdapter.ChefKeyAuthRow(
            "AppRegistrationCertificate", "00000000-0000-0000-0000-000000000001", "ABCDEF0123456789");

        var personal = RecipeWithAuth($$"""
        {
          "mode": "AppRegistrationCertificate",
          "tenantId": "{{TenantId}}",
          "chefKeyId": "personal-key-1"
        }
        """);

        List<string> personalArgv = PaxAdapter.GetArgvArray(personal, chefKey, "local-manual");
        List<string> organizationArgv = PaxAdapter.GetArgvArray(OrganizationBoundRecipe(), chefKey, "local-manual");

        // The organization binding contributes ZERO tokens: argv is identical.
        Assert.Equal(personalArgv, organizationArgv);

        // There is no organization credential path at all: without a resolved
        // PERSONAL Chef's Key the projection refuses rather than substituting one.
        Assert.Throws<PaxAdapter.ProjectionException>(
            () => PaxAdapter.GetArgvArray(OrganizationBoundRecipe(), chefKey: null, executionMode: "local-manual"));
    }

    [Fact] // T24 — personal Chef's Key binding behavior is unchanged.
    public void T24_PersonalBindingBehavior_Unchanged()
    {
        // Personal binding is still valid on every mode that allowed it before,
        // and still uses its own (narrower) identifier charset.
        foreach (string mode in new[] { "WebLogin", "DeviceCode" })
        {
            var recipe = RecipeWithAuth($$"""{ "mode": "{{mode}}", "chefKeyId": "personal-key-1" }""");
            Assert.True(RecipeValidationModel.ValidateAll(recipe).Ok, mode);
        }

        // The personal pattern does NOT accept the wider organization charset.
        var dotted = RecipeWithAuth("""{ "mode": "WebLogin", "chefKeyId": "has.dot" }""");
        Assert.False(RecipeValidationModel.ValidateAll(dotted).Ok);

        // App-registration recipes are still saveable WITHOUT any binding.
        var unbound = RecipeWithAuth($$"""
        { "mode": "AppRegistrationCertificate", "tenantId": "{{TenantId}}" }
        """);
        Assert.True(RecipeValidationModel.ValidateAll(unbound).Ok);
    }

    [Fact] // T25 — existing Recipe compatibility: unknown auth fields still fail closed.
    public void T25_ExistingRecipeCompatibility()
    {
        // The closed schema is preserved: a genuinely unknown auth field is still
        // rejected as an additional property.
        var unknown = OrganizationBoundRecipe();
        ((Dictionary<string, object?>)unknown["auth"]!)["someNewField"] = "x";
        (bool ok, List<object> errors) = RecipeValidationModel.ValidateAll(unknown);
        Assert.False(ok);
        Assert.True(HasKeyword(errors, "additionalProperties"), Fmt(errors));

        // A legacy recipe carrying the deprecated authProfileId still loads.
        var legacy = RecipeWithAuth("""{ "mode": "WebLogin", "authProfileId": "legacy-1" }""");
        Assert.True(RecipeValidationModel.ValidateAll(legacy).Ok);

        // App-registration still requires a declared tenant.
        var noTenant = RecipeWithAuth($$"""
        { "mode": "AppRegistrationCertificate", "organizationKeyId": "{{OrgKeyId}}" }
        """);
        Assert.False(RecipeValidationModel.ValidateAll(noTenant).Ok);
    }
}
