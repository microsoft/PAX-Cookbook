using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using PAXCookbook.App;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle 14s — ORGANIZATION-KEY SELECTOR + NOT-YET-RUNNABLE.
//
// The immutable PAX engine can only select a certificate by SHA-1 thumbprint, so
// organization-bound Cook CANNOT work. This suite proves the safe half only:
// a bounded selector projection, an opaque-id-only Recipe binding, and an
// EXPLICIT, TRUTHFUL refusal before Cook/PAX preparation. It derives NO SHA-1
// thumbprint, constructs NO argv, reaches NO PaxAdapter engine selector, spawns
// NO process, opens no real certificate store, reads no key or secret, contacts
// no tenant/Graph/service, runs no PAX, and performs no Bake. Every inventory
// document is an in-memory string and every catalog occurrence is a hand-built
// bounded record.
public sealed class OrganizationKeyNotYetRunnableTests
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
    internal const string OrgKeyId = "contoso.managed_key-1";

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

    private static ManagedChefKeysGateProjection AuthorizedGate()
        => ManagedChefKeysGate.Evaluate(
            MachinePolicyDetection.ConfiguredOrganizationManaged(
                MachinePolicyCapability.Enabled, MachinePolicyCapability.Disabled));

    // Distinct per entry: the inventory parser rejects a document that reuses a
    // certificate reference across two entries.
    internal const string FingerprintA =
        "A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1A1";
    internal const string FingerprintB =
        "B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2B2";
    internal const string FingerprintC =
        "C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3C3";

    private static string Common(string id, string adminState, string displayName)
        => ", \"organizationKeyId\": \"" + id + "\""
           + ", \"displayName\": \"" + displayName + "\""
           + ", \"certificateReferenceType\": \"app_registration_certificate\""
           + ", \"adminState\": \"" + adminState + "\""
           + ", \"tenantReference\": \"tenant-ref-1\""
           + ", \"clientReference\": \"client-ref-1\"";

    internal static string V2Entry(
        string id, string fingerprint, string adminState = "enabled", string displayName = "Contoso Managed Key")
        => "{ \"entryVersion\": 2" + Common(id, adminState, displayName)
           + ", \"certificateSha256\": \"" + fingerprint.ToLowerInvariant() + "\" }";

    internal static string Doc(int schemaVersion, params string[] entries)
        => "{ \"schemaVersion\": " + schemaVersion + ", \"entries\": [" + string.Join(",", entries) + "] }";

    internal static OrganizationInventoryEvaluation Provisioned(string document)
        => OrganizationKeyInventoryEvaluator.EvaluateDetailed(
            AuthorizedGate(), new InMemorySource(OrganizationInventorySourceResult.Provided(document)));

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    internal static ICertificateUsabilityClock Clock() => new FixedCertificateUsabilityClock(Now);

    private const string ClientAuthOid =
        OrganizationCertificateUsabilityContract.ClientAuthenticationPurposeOid;

    internal static CertificateUsabilityFacts UsableFacts() => CertificateUsabilityFacts.Create(
        Now.AddDays(-1), Now.AddDays(30),
        CertificateKeyAlgorithmClass.Rsa, CertificateKeyAvailability.Available,
        purposeExtensionPresent: true, purposeOids: new[] { ClientAuthOid },
        keyUsageExtensionPresent: true, digitalSignatureAllowed: true);

    internal static CertificateUsabilityFacts ExpiredFacts() => CertificateUsabilityFacts.Create(
        Now.AddDays(-40), Now.AddDays(-10),
        CertificateKeyAlgorithmClass.Rsa, CertificateKeyAvailability.Available,
        purposeExtensionPresent: true, purposeOids: new[] { ClientAuthOid },
        keyUsageExtensionPresent: true, digitalSignatureAllowed: true);

    internal static ICertificateCatalog CatalogWith(
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

    private static VersionInfo Version() => new(
        CookbookVersion: "2.0.0",
        ReleaseChannel: "stable",
        PaxVersion: "1.11.14",
        PaxSha256: "0000000000000000000000000000000000000000000000000000000000000000",
        PaxRelativePath: "Engine/pax.ps1",
        PaxAcquisitionPolicy: "managed",
        EngineManifestUrl: null,
        EngineManifestTrustAnchorThumbprint: null,
        ManifestSignaturePolicy: "none",
        BuildTimestamp: null);

    // Cycle 16 — the preview projection now REQUIRES an organization authority.
    // These tests supply the STRONGEST possible local input: a provisioned,
    // trusted inventory whose single enabled entry resolves to exactly one usable
    // certificate. Every refusal below therefore proves the refusal survives a
    // fully locally-ready binding, not merely a missing dependency.
    private static Func<RecipeReadModel.OrganizationCookPreparationContext> ReadyLocalAuthority(
        EngineCapabilityState capability = EngineCapabilityState.NotDeclared)
        => () => new RecipeReadModel.OrganizationCookPreparationContext(
            () => capability,
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            CatalogWith((FingerprintA, UsableFacts())),
            Clock());

    // =========================================================================
    // TEST-FIRST anchor. Selecting an ELIGIBLE organization key stores ONLY the
    // opaque organizationKeyId AND leaves the Recipe explicitly NOT YET RUNNABLE:
    // local readiness is `organization_key_ready`, but the run pipeline refuses
    // with `organization_key_not_yet_runnable` and projects no command at all.
    // =========================================================================
    [Fact]
    public void TF01_EligibleOrganizationKey_StoresOnlyOpaqueId_AndIsNotYetRunnable()
    {
        Dictionary<string, object?> recipe = OrganizationBoundRecipe();

        // 1. The Recipe persists ONLY the opaque identifier: no personal key, no
        //    thumbprint, no certificate reference, no client id.
        var auth = (Dictionary<string, object?>)recipe["auth"]!;
        Assert.Equal(OrgKeyId, auth["organizationKeyId"]);
        Assert.False(auth.ContainsKey("chefKeyId"));
        Assert.False(auth.ContainsKey("certificateThumbprint"));
        Assert.False(auth.ContainsKey("clientId"));

        // 2. LOCAL readiness says the binding is set up on this PC.
        OrganizationKeyBindingReadiness? readiness = RecipeReadinessModel.ProjectOrganizationKeyBinding(
            recipe,
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            CatalogWith((FingerprintA, UsableFacts())),
            Clock());
        Assert.NotNull(readiness);
        Assert.True(readiness!.Ready);
        Assert.Equal("organization_key_ready", readiness.WireState);

        // 3. Locally ready is NOT runnable. The shared validate + projection
        //    pipeline refuses with the explicit not-yet-runnable execution status
        //    and produces NO invocation plan.
        RecipePreviewModel.ProjectionResult projection = RecipePreviewModel.Project(
            "C:\\PAXCookbookTestWorkspace", "C:\\PAXCookbookTestEngine\\pax.ps1", Version(), recipe,
            ReadyLocalAuthority());

        Assert.False(projection.Ok);
        Assert.Null(projection.Plan);
        Assert.Null(projection.AuthRow);

        string body = JsonSerializer.Serialize(projection.ErrorBody);
        Assert.Contains("organization_key_not_yet_runnable", body, StringComparison.Ordinal);
        Assert.Contains("This organization-provided certificate is not yet available for Bakes.", body, StringComparison.Ordinal);
        // It never falls back to the personal key path or invents a thumbprint.
        Assert.DoesNotContain("chefKeyId", body, StringComparison.Ordinal);
        Assert.DoesNotContain("humbprint", body, StringComparison.Ordinal);
    }

    // =========================================================================
    // A. Bounded route projection (matrix 1-7).
    // =========================================================================

    private static readonly string[] AllowedSelectorFields = { "organizationKeyId", "displayName", "eligible" };

    private static (JsonElement Root, string Json) WireList(
        OrganizationInventoryEvaluation evaluation,
        ICertificateCatalog? catalog,
        ICertificateUsabilityClock? clock)
    {
        OrganizationCertificateAggregate aggregate =
            OrganizationCertificateResolutionCoordinator.Resolve(evaluation, catalog, clock);
        (int status, object body) = ChefKeyModel.List(
            evaluation.Projection,
            aggregate,
            OrganizationKeySelectorProjection.Build(evaluation, catalog, clock));
        Assert.Equal(200, status);
        string json = JsonSerializer.Serialize(body);
        using var doc = JsonDocument.Parse(json);
        return (doc.RootElement.Clone(), json);
    }

    private static JsonElement Selector(JsonElement root)
    {
        Assert.True(root.TryGetProperty("organizationKeys", out JsonElement org));
        Assert.True(org.TryGetProperty("selectableKeys", out JsonElement selector), "selectableKeys was omitted");
        Assert.Equal(JsonValueKind.Array, selector.ValueKind);
        return selector;
    }

    private static bool HasSelector(JsonElement root)
        => root.TryGetProperty("organizationKeys", out JsonElement org)
           && org.TryGetProperty("selectableKeys", out _);

    [Fact] // matrix 1 — each element carries EXACTLY the three allowed fields.
    public void S01_SelectorElement_CarriesExactlyThreeAllowedFields()
    {
        (JsonElement root, string json) = WireList(
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            CatalogWith((FingerprintA, UsableFacts())),
            Clock());

        JsonElement element = Assert.Single(Selector(root).EnumerateArray());
        var names = new List<string>();
        foreach (JsonProperty p in element.EnumerateObject()) { names.Add(p.Name); }
        Assert.Equal(AllowedSelectorFields.Length, names.Count);
        foreach (string allowed in AllowedSelectorFields) { Assert.Contains(allowed, names); }

        Assert.Equal(OrgKeyId, element.GetProperty("organizationKeyId").GetString());
        Assert.Equal("Contoso Managed Key", element.GetProperty("displayName").GetString());
        Assert.True(element.GetProperty("eligible").GetBoolean());

        // Nothing else about the entry reaches the wire.
        foreach (string forbidden in new[]
        {
            "tenantReference", "clientReference", "tenantId", "clientId",
            "certificateSha256", "certificateReference", "certificateReferenceType",
            "thumbprint", "certThumbprint", "subject", "issuer", "serial",
            "store", "storeLocation", "adminState", "reason", "privateKey",
            "path", "secret", "clientSecret", "token", "claim",
        })
        {
            Assert.False(element.TryGetProperty(forbidden, out _), $"selector leaked '{forbidden}'");
        }
        Assert.DoesNotContain("tenant-ref", json, StringComparison.Ordinal);
        Assert.DoesNotContain("client-ref", json, StringComparison.Ordinal);
        Assert.DoesNotContain(FingerprintA.ToLowerInvariant(), json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // matrix 2 — administrator-disabled entries are never offered.
    public void S02_DisabledEntries_AreOmittedEntirely()
    {
        (JsonElement root, _) = WireList(
            Provisioned(Doc(2,
                V2Entry("enabled.key", FingerprintA, displayName: "Enabled Key"),
                V2Entry("disabled.key", FingerprintB, adminState: "disabled", displayName: "Disabled Key"))),
            CatalogWith((FingerprintA, UsableFacts()), (FingerprintB, UsableFacts())),
            Clock());

        JsonElement element = Assert.Single(Selector(root).EnumerateArray());
        Assert.Equal("enabled.key", element.GetProperty("organizationKeyId").GetString());
    }

    [Fact] // matrix 3 — ordinal displayName, then organizationKeyId. Deterministic.
    public void S03_Ordering_IsStableOrdinalByDisplayNameThenId()
    {
        (JsonElement root, _) = WireList(
            Provisioned(Doc(2,
                V2Entry("zulu.key", FingerprintA, displayName: "Beta"),
                V2Entry("alpha.key", FingerprintB, displayName: "Beta"),
                V2Entry("mike.key", FingerprintC, displayName: "Alpha"))),
            CatalogWith(
                (FingerprintA, UsableFacts()), (FingerprintB, UsableFacts()), (FingerprintC, UsableFacts())),
            Clock());

        var order = new List<string>();
        foreach (JsonElement e in Selector(root).EnumerateArray())
        {
            order.Add(e.GetProperty("displayName").GetString() + "|" + e.GetProperty("organizationKeyId").GetString());
        }
        Assert.Equal(new[] { "Alpha|mike.key", "Beta|alpha.key", "Beta|zulu.key" }, order);
    }

    [Fact] // matrix 4 — a duplicate id fails the ORGANIZATION projection closed; personal still renders.
    public void S04_DuplicateId_FailsOrganizationProjectionClosed_PersonalStillRenders()
    {
        // The real parser refuses a duplicate id, so no inventory is provisioned
        // and nothing at all becomes selectable.
        OrganizationInventoryEvaluation evaluation = Provisioned(
            Doc(2, V2Entry(OrgKeyId, FingerprintA), V2Entry(OrgKeyId.ToUpperInvariant(), FingerprintB)));
        Assert.NotEqual(OrganizationInventoryState.AuthorizedProvisioned, evaluation.Projection.State);

        IReadOnlyList<OrganizationKeySelectorEntry> selector = OrganizationKeySelectorProjection.Build(
            evaluation, CatalogWith((FingerprintA, UsableFacts())), Clock());
        Assert.Empty(selector);

        (JsonElement root, _) = WireList(evaluation, CatalogWith((FingerprintA, UsableFacts())), Clock());
        Assert.False(HasSelector(root), "a duplicate id must not produce a selector array");
        // The personal array is a separate projection and still renders.
        Assert.True(root.TryGetProperty("chefKeys", out JsonElement chefKeys));
        Assert.Equal(JsonValueKind.Array, chefKeys.ValueKind);
    }

    [Fact] // matrix 5 — the personal chefKeys array is byte-identical with and without the selector.
    public void S05_PersonalArray_IsUnchangedByTheSelector()
    {
        OrganizationInventoryEvaluation evaluation = Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA)));
        ICertificateCatalog catalog = CatalogWith((FingerprintA, UsableFacts()));
        OrganizationCertificateAggregate aggregate =
            OrganizationCertificateResolutionCoordinator.Resolve(evaluation, catalog, Clock());

        (_, object withoutSelector) = ChefKeyModel.List(evaluation.Projection, aggregate);
        (_, object withSelector) = ChefKeyModel.List(
            evaluation.Projection, aggregate,
            OrganizationKeySelectorProjection.Build(evaluation, catalog, Clock()));

        using var a = JsonDocument.Parse(JsonSerializer.Serialize(withoutSelector));
        using var b = JsonDocument.Parse(JsonSerializer.Serialize(withSelector));
        Assert.Equal(
            a.RootElement.GetProperty("chefKeys").GetRawText(),
            b.RootElement.GetProperty("chefKeys").GetRawText());
        // Only the nested selector array was added to organizationKeys.
        Assert.False(a.RootElement.GetProperty("organizationKeys").TryGetProperty("selectableKeys", out _));
        Assert.True(b.RootElement.GetProperty("organizationKeys").TryGetProperty("selectableKeys", out _));
    }

    [Fact] // matrix 6 + 14 — no new route, no mutation route, no organization control.
    public void S06_NoNewRoute_AndNoOrganizationMutationRoute()
    {
        string program = ReadSource("src/PAXCookbook.App/Program.cs");
        // The selector is projected on the EXISTING chef-keys list route only.
        Assert.Contains("OrganizationKeySelectorProjection.Build(", program, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(program, "MapGet(\"/api/v1/chef-keys\""));
        foreach (string forbidden in new[]
        {
            "\"/api/v1/organization", "chef-keys/organization", "organization-keys\"",
            "MapPost(\"/api/v1/chef-keys/organization",
            "MapPut(\"/api/v1/chef-keys/organization",
            "MapDelete(\"/api/v1/chef-keys/organization",
        })
        {
            Assert.DoesNotContain(forbidden, program, StringComparison.Ordinal);
        }

        // The projection itself is read-only: it exposes only a Build method.
        string projection = ReadSource("src/PAXCookbook.App/OrganizationKeySelectorProjection.cs");
        foreach (string forbidden in new[] { "File.Write", "File.Delete", "Directory.Create", "Registry", "SetAccessControl" })
        {
            Assert.DoesNotContain(forbidden, projection, StringComparison.Ordinal);
        }
    }

    [Fact] // matrix 7 — the two narrowed names appear ONLY inside the nested selector array.
    public void S07_NarrowedNames_AppearOnlyInsideTheNestedSelectorArray()
    {
        (JsonElement root, string json) = WireList(
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            CatalogWith((FingerprintA, UsableFacts())),
            Clock());

        // Not a direct property of organizationKeys, and not on the root.
        JsonElement org = root.GetProperty("organizationKeys");
        Assert.False(org.TryGetProperty("organizationKeyId", out _));
        Assert.False(org.TryGetProperty("displayName", out _));
        Assert.False(root.TryGetProperty("organizationKeyId", out _));

        // Outside the exact nested array, neither name nor either value survives.
        string outside = RemoveSelectorArray(json);
        Assert.DoesNotContain("organizationKeyId", outside, StringComparison.Ordinal);
        Assert.DoesNotContain("\"displayName\":\"Contoso Managed Key\"", outside, StringComparison.Ordinal);
        Assert.DoesNotContain(OrgKeyId, outside, StringComparison.Ordinal);

        // The projection-only overload (no selector) still bans them everywhere.
        (int _, object plain) = ChefKeyModel.List(Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))).Projection);
        string plainJson = JsonSerializer.Serialize(plain);
        Assert.DoesNotContain("selectableKeys", plainJson, StringComparison.Ordinal);
        Assert.DoesNotContain(OrgKeyId, plainJson, StringComparison.Ordinal);
    }

    [Fact] // matrix 13 — an ineligible entry is listed but never eligible.
    public void S08_IneligibleEntry_IsProjectedNotEligible()
    {
        (JsonElement root, _) = WireList(
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            CatalogWith((FingerprintA, ExpiredFacts())),
            Clock());

        JsonElement element = Assert.Single(Selector(root).EnumerateArray());
        Assert.False(element.GetProperty("eligible").GetBoolean());
        // The refusal category is never restated on the wire.
        Assert.False(element.TryGetProperty("reason", out _));
    }

    [Fact] // the array is OMITTED entirely when nothing is selectable.
    public void S09_SelectorArray_IsOmittedWhenNothingIsSelectable()
    {
        // No enabled entry at all.
        (JsonElement disabledOnly, _) = WireList(
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA, adminState: "disabled"))),
            CatalogWith((FingerprintA, UsableFacts())),
            Clock());
        Assert.False(HasSelector(disabledOnly));

        // Empty provisioned inventory.
        (JsonElement empty, _) = WireList(
            Provisioned(Doc(2)), CatalogWith((FingerprintA, UsableFacts())), Clock());
        Assert.False(HasSelector(empty));

        // Not provisioned at all.
        Assert.Empty(OrganizationKeySelectorProjection.Build(null, null, Clock()));
    }

    [Fact] // the whole request opens the certificate catalog AT MOST once.
    public void S10_Catalog_IsQueriedAtMostOncePerRequest()
    {
        var counting = new CountingCatalog(
            CatalogWith((FingerprintA, UsableFacts()), (FingerprintB, UsableFacts())));
        var shared = new OnceQueriedCertificateCatalog(counting);
        OrganizationInventoryEvaluation evaluation = Provisioned(Doc(2,
            V2Entry("a.key", FingerprintA, displayName: "A"),
            V2Entry("b.key", FingerprintB, displayName: "B")));

        OrganizationCertificateResolutionCoordinator.Resolve(evaluation, shared, Clock());
        OrganizationKeySelectorProjection.Build(evaluation, shared, Clock());

        Assert.Equal(1, counting.QueryCount);
    }

    private sealed class CountingCatalog : ICertificateCatalog
    {
        private readonly ICertificateCatalog _inner;

        public CountingCatalog(ICertificateCatalog inner) { _inner = inner; }

        public int QueryCount { get; private set; }

        public CertificateCatalogResult Query()
        {
            QueryCount++;
            return _inner.Query();
        }
    }

    // =========================================================================
    // B. Cook / PAX hard block (matrix 18, 20, 21, 22, 23, 24).
    // =========================================================================

    [Fact] // matrix 18 + 20 — gate 14 refuses an organization binding with the bounded status.
    public void C01_CookPreparation_BlocksWithBoundedNotYetRunnableBody()
    {
        (int status, object? body, bool hasRow) = RecipeReadModel.TestSeamResolveAuthForProjection(
            "AppRegistrationCertificate", null, OrgKeyId);

        Assert.Equal(412, status);
        Assert.False(hasRow);
        Assert.NotNull(body);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        JsonElement root = doc.RootElement;
        Assert.Equal("recipe_invalid", root.GetProperty("error").GetString());
        Assert.Equal("organization_key_not_yet_runnable", root.GetProperty("executionStatus").GetString());

        JsonElement error = Assert.Single(root.GetProperty("errors").EnumerateArray());
        Assert.Equal("/auth/organizationKeyId", error.GetProperty("path").GetString());
        Assert.Equal("organizationKeyNotYetRunnable", error.GetProperty("keyword").GetString());
        Assert.Equal(
            "This organization-provided certificate is not yet available for Bakes.",
            error.GetProperty("message").GetString());
        Assert.Equal(
            "PAX Cookbook is waiting for a fail-closed engine certificate selector.",
            error.GetProperty("params").GetProperty("detail").GetString());
    }

    [Fact] // matrix 21 — never falls back to a personal key, even when one is also present.
    public void C02_CookPreparation_NeverFallsBackToAPersonalKey()
    {
        (int status, object? body, bool hasRow) = RecipeReadModel.TestSeamResolveAuthForProjection(
            "AppRegistrationCertificate", "some-personal-key", OrgKeyId);

        Assert.Equal(412, status);
        Assert.False(hasRow);
        string json = JsonSerializer.Serialize(body);
        Assert.Contains("organization_key_not_yet_runnable", json, StringComparison.Ordinal);
        // The organization refusal wins; it never resolves or names the personal key.
        Assert.DoesNotContain("some-personal-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("chefKeyNotFound", json, StringComparison.Ordinal);
    }

    [Fact] // matrix 22 — no SHA-1 / SHA-256 selector, thumbprint, or identifier in the refusal.
    public void C03_Refusal_CarriesNoSelectorMaterialAtAll()
    {
        (_, object? body, _) = RecipeReadModel.TestSeamResolveAuthForProjection(
            "AppRegistrationCertificate", null, OrgKeyId);
        string json = JsonSerializer.Serialize(body);

        foreach (string forbidden in new[]
        {
            OrgKeyId, FingerprintA, "humbprint", "SHA1", "SHA-1", "Sha1",
            "SHA256", "SHA-256", "ClientCertificateThumbprint", "CurrentUser", "LocalMachine",
        })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact] // matrix 23 + 28 — the block is CAPABILITY-GATED and still precedes plan projection and every process construction.
    public void C04_Block_IsCapabilityGated_AndPrecedesArgvProjectionAndProcessConstruction()
    {
        string cookStart = ReadSource("src/PAXCookbook.App/RecipeReadModel.CookStart.cs");
        int block = cookStart.IndexOf("OrganizationKeyRunnability.ExecutionStatus", StringComparison.Ordinal);
        Assert.True(block > 0, "the organization hard block is missing");

        // Everything that could build a command or a process comes strictly later
        // in the gate-14 helper's own body, and gate 14 already runs before them.
        int helper = cookStart.IndexOf(
            "private static (int Status, object? Body, PaxAdapter.ChefKeyAuthRow? ChefKey, ChefKeyModel.ChefKeyResolved? Resolved) ResolveChefKeyForProjection",
            StringComparison.Ordinal);
        Assert.True(helper > 0 && block > helper, "the block must live inside the gate-14 helper");

        // The gate-14 call site precedes gate 15's plan projection.
        int gate14Call = cookStart.IndexOf(
            "ResolveChefKeyForProjection(recipe, recipeId, organizationPreparation)", StringComparison.Ordinal);
        int planCall = cookStart.IndexOf("PaxAdapter.GetInvocationPlan(", StringComparison.Ordinal);
        Assert.True(gate14Call > 0 && planCall > gate14Call, "gate 14 must precede the invocation plan");

        // Cycle 15 + 16 — the engine capability is resolved BEFORE gate 14 runs
        // and the organization branch is gated on it rather than being
        // unconditional. Cycle 16 makes the needle MORE specific: the capability
        // must come from the SINGLE production authority factory, which must
        // itself evaluate the sanctioned capability token.
        int capability = cookStart.IndexOf(
            "ProductionOrganizationAuthority.CreateSnapshot(versionInfo, engine)", StringComparison.Ordinal);
        Assert.True(capability > 0 && capability < gate14Call,
            "the engine capability must be resolved before gate 14");
        Assert.Contains("EngineCapabilityState.Available", cookStart, StringComparison.Ordinal);

        string authority = ReadSource("src/PAXCookbook.App/ProductionOrganizationAuthority.cs");
        Assert.Contains("EngineCapabilityRuntime.Evaluate(", authority, StringComparison.Ordinal);
        Assert.Contains(
            "EngineCapabilityRuntime.OrganizationCertificateSha256Selector",
            authority, StringComparison.Ordinal);

        // The helper and everything after it construct nothing. (Cycle 15
        // strengthens this: the scanned region is the rest of the file, not an
        // empty slice.)
        string helperBody = cookStart.Substring(helper);
        Assert.True(helperBody.Length > 0, "the helper body must be scannable");
        foreach (string forbidden in new[]
        {
            "new Process(", "Process.Start(", "ProcessStartInfo",
            "ClientCertificateThumbprint", "SHA1", "Sha1",
        })
        {
            Assert.DoesNotContain(forbidden, helperBody, StringComparison.Ordinal);
        }
    }

    [Theory] // matrix 24 — personal behaviour is completely unchanged.
    [InlineData("WebLogin")]
    [InlineData("DeviceCode")]
    [InlineData("")]
    public void C05_NonAppRegistrationModes_StillPassThroughUnchanged(string authMode)
    {
        (int status, object? body, bool hasRow) =
            RecipeReadModel.TestSeamResolveAuthForProjection(authMode, null, null);
        Assert.Equal(200, status);
        Assert.Null(body);
        Assert.False(hasRow);
    }

    [Fact] // matrix 24 — an App-registration recipe with NO organization binding keeps its old error.
    public void C06_PersonalAppRegistrationWithoutKey_KeepsItsExistingBoundedError()
    {
        (int status, object? body, bool hasRow) = RecipeReadModel.TestSeamResolveAuthForProjection(
            "AppRegistrationCertificate", null, null);

        Assert.Equal(412, status);
        Assert.False(hasRow);
        string json = JsonSerializer.Serialize(body);
        Assert.Contains("no chefKeyId is set", json, StringComparison.Ordinal);
        Assert.DoesNotContain("organization_key_not_yet_runnable", json, StringComparison.Ordinal);
    }

    // =========================================================================
    // C. Preview (matrix 19, 24).
    // =========================================================================

    [Fact] // matrix 19 — an organization-bound preview emits NO executable command.
    public void P01_Preview_EmitsNoExecutableCommand()
    {
        RecipePreviewModel.ProjectionResult projection = RecipePreviewModel.Project(
            "C:\\PAXCookbookTestWorkspace", "C:\\PAXCookbookTestEngine\\pax.ps1", Version(),
            OrganizationBoundRecipe(), ReadyLocalAuthority());

        Assert.False(projection.Ok);
        Assert.Null(projection.Plan);
        Assert.Null(projection.AuthRow);

        string json = JsonSerializer.Serialize(projection.ErrorBody);
        Assert.DoesNotContain("pax.ps1", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Auth", json, StringComparison.Ordinal);
        Assert.DoesNotContain(OrgKeyId, json, StringComparison.Ordinal);
    }

    [Fact] // matrix 24 — a personal recipe's preview path is untouched.
    public void P02_PersonalPreview_IsUnchanged()
    {
        Dictionary<string, object?> recipe = RecipeWithAuth($$"""
        {
          "mode": "AppRegistrationCertificate",
          "tenantId": "{{TenantId}}",
          "chefKeyId": "no-such-personal-key"
        }
        """);

        RecipePreviewModel.ProjectionResult projection = RecipePreviewModel.Project(
            "C:\\PAXCookbookTestWorkspace", "C:\\PAXCookbookTestEngine\\pax.ps1", Version(), recipe,
            ReadyLocalAuthority());

        Assert.False(projection.Ok);
        string json = JsonSerializer.Serialize(projection.ErrorBody);
        // The pre-existing personal error is preserved verbatim.
        Assert.Contains("chefKeyNotFound", json, StringComparison.Ordinal);
        Assert.DoesNotContain("organization_key_not_yet_runnable", json, StringComparison.Ordinal);
    }

    // =========================================================================
    // D. Containment scan over the CHANGED production files.
    // =========================================================================

    [Fact] // no SHA-1 mapping, no engine selector change, no process construction.
    public void D01_ChangedFiles_ContainNoSelectorMappingOrProcessConstruction()
    {
        foreach (string relative in new[]
        {
            "src/PAXCookbook.App/OrganizationKeySelectorProjection.cs",
            "src/PAXCookbook.App/OrganizationKeyRecipeBindingContract.cs",
        })
        {
            // Comments are stripped so descriptive doctrine prose cannot
            // false-positive a CODE-pattern scan.
            string src = StripComments(ReadSource(relative));
            foreach (string forbidden in new[]
            {
                "Thumbprint", "SHA1", "Sha1", "X509Certificate", "X509Store",
                "PaxAdapter", "Process.Start", "new Process(", "ProcessStartInfo",
                "WindowsCredentialStore", "HttpClient", "Graph",
            })
            {
                Assert.DoesNotContain(forbidden, src, StringComparison.Ordinal);
            }
        }
    }

    // =========================================================================
    // E. Cycle 15 — capability-gated organization Cook preparation (matrix 21-29).
    // =========================================================================

    private static (int Status, object? Body, PaxAdapter.ChefKeyAuthRow? Row) Prepare(
        EngineCapabilityState capability)
        => RecipeReadModel.TestSeamResolveOrganizationCookPreparation(
            OrgKeyId,
            capability,
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            CatalogWith((FingerprintA, UsableFacts())),
            Clock());

    [Fact] // matrix 21 — a FULLY READY Recipe is still not runnable without the capability.
    public void E01_FullyReadyRecipe_IsNotYetRunnable_WithoutTheCapability()
    {
        foreach (EngineCapabilityState capability in new[]
        {
            EngineCapabilityState.NotDeclared,
            EngineCapabilityState.EngineNotAcquired,
            EngineCapabilityState.EngineHashMismatch,
            EngineCapabilityState.EngineVersionMismatch,
            EngineCapabilityState.StateInvalid,
            EngineCapabilityState.Unknown,
        })
        {
            (int status, object? body, PaxAdapter.ChefKeyAuthRow? row) = Prepare(capability);

            Assert.Equal(412, status);
            Assert.Null(row);

            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
            JsonElement root = doc.RootElement;
            Assert.Equal("organization_key_not_yet_runnable", root.GetProperty("executionStatus").GetString());
            JsonElement error = Assert.Single(root.GetProperty("errors").EnumerateArray());
            Assert.Equal(
                "This organization-provided certificate is not yet available for Bakes.",
                error.GetProperty("message").GetString());
        }
    }

    [Fact] // matrix 22 — with the capability, canonical certificate-auth preparation succeeds.
    public void E02_CapableEngine_ProducesCanonicalCertificatePreparation()
    {
        (int status, object? body, PaxAdapter.ChefKeyAuthRow? row) =
            Prepare(EngineCapabilityState.Available);

        Assert.Equal(200, status);
        Assert.Null(body);
        Assert.NotNull(row);
        Assert.Equal("AppRegistrationCertificate", row!.Mode);
        // The ALREADY-EXISTING inventory reference, normalized to uppercase. No
        // new hash is computed and no SHA-1 thumbprint is derived.
        Assert.Equal(FingerprintA, row.CertSha256);
        Assert.Null(row.CertThumbprint);
    }

    [Fact] // matrix 23 + 24 + 25 + 26 — the new argv is the SHA-256 selector and nothing else.
    public void E03_CapablePreparation_ProjectsTheSha256SelectorOnly()
    {
        (_, _, PaxAdapter.ChefKeyAuthRow? row) = Prepare(EngineCapabilityState.Available);
        Assert.NotNull(row);

        List<string> argv = PaxAdapter.GetArgvArray(OrganizationBoundRecipe(), row, "local-manual");

        int selector = argv.IndexOf("-ClientCertificateSha256");
        Assert.True(selector >= 0, "the SHA-256 selector was not emitted");
        Assert.Equal(FingerprintA, argv[selector + 1]);

        // matrix 24 — the legacy SHA-1 parameter is never also emitted.
        Assert.DoesNotContain("-ClientCertificateThumbprint", argv);

        string joined = string.Join(" ", argv);
        // matrix 25 — no CurrentUser store selector anywhere.
        Assert.DoesNotContain("CurrentUser", joined, StringComparison.OrdinalIgnoreCase);
        // matrix 26 — no organization identifier and no display name.
        Assert.DoesNotContain(OrgKeyId, joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Contoso Managed Key", joined, StringComparison.Ordinal);
    }

    [Fact] // matrix 27 — no SHA-256 reaches the preview / bounded readiness output.
    public void E04_PreviewAndReadiness_NeverExposeTheCertificateSha256()
    {
        RecipePreviewModel.ProjectionResult projection = RecipePreviewModel.Project(
            "C:\\PAXCookbookTestWorkspace", "C:\\PAXCookbookTestEngine\\pax.ps1", Version(),
            OrganizationBoundRecipe(), ReadyLocalAuthority());
        string previewJson = JsonSerializer.Serialize(projection.ErrorBody);

        OrganizationKeyBindingReadiness? readiness = RecipeReadinessModel.ProjectOrganizationKeyBinding(
            OrganizationBoundRecipe(),
            Provisioned(Doc(2, V2Entry(OrgKeyId, FingerprintA))),
            CatalogWith((FingerprintA, UsableFacts())),
            Clock());

        foreach (string surface in new[]
        {
            previewJson, readiness!.ToString(), readiness.WireState, readiness.Detail,
        })
        {
            Assert.DoesNotContain(FingerprintA, surface, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ClientCertificateSha256", surface, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SHA-256", surface, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact] // matrix 29 — the PERSONAL certificate Cook path is byte-identical.
    public void E05_PersonalCertificatePath_StillEmitsTheThumbprintSelector()
    {
        var personal = new PaxAdapter.ChefKeyAuthRow(
            "AppRegistrationCertificate", "00000000-0000-0000-0000-000000000001", "ABCDEF0123456789");

        Dictionary<string, object?> recipe = RecipeWithAuth($$"""
        {
          "mode": "AppRegistrationCertificate",
          "tenantId": "{{TenantId}}",
          "chefKeyId": "personal-key-1"
        }
        """);

        List<string> argv = PaxAdapter.GetArgvArray(recipe, personal, "local-manual");

        int thumb = argv.IndexOf("-ClientCertificateThumbprint");
        Assert.True(thumb >= 0, "the personal thumbprint selector regressed");
        Assert.Equal("ABCDEF0123456789", argv[thumb + 1]);
        Assert.DoesNotContain("-ClientCertificateSha256", argv);
    }

    [Fact] // the new selector can never be injected through the recipe trailer.
    public void E06_ClientCertificateSha256_IsForbiddenInTheRecipeTrailer()
    {
        Assert.Throws<PaxAdapter.ProjectionException>(
            () => PaxAdapter.ScanSecretShape("-ClientCertificateSha256 " + FingerprintA));
    }

    // ---- helpers -------------------------------------------------------------

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        string dir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\r\n]*", " ");
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // Removes the exact nested `"selectableKeys":[ ... ]` segment so a ban can be
    // applied to everything OUTSIDE that one permitted shape.
    private static string RemoveSelectorArray(string json)
    {
        const string marker = "\"selectableKeys\":[";
        int start = json.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) { return json; }
        int depth = 0;
        for (int i = start + marker.Length - 1; i < json.Length; i++)
        {
            if (json[i] == '[') { depth++; }
            else if (json[i] == ']')
            {
                depth--;
                if (depth == 0) { return json.Remove(start, i - start + 1); }
            }
        }
        return json;
    }
}
