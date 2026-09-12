using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle 16 — PRODUCTION ORGANIZATION AUTHORITY WIRING.
//
// Proves that the REAL machine policy, trusted ProgramData inventory, machine
// certificate catalog, system clock, and acquired-engine capability are the
// dependencies production actually evaluates for an organization-bound Recipe,
// and that organization Bakes are STILL blocked because the active engine
// declares no sanctioned SHA-256 selector capability.
//
// Nothing here opens a real certificate store, reads a private key or secret,
// contacts a tenant/Graph/service, mutates ProgramData/ACLs/registry, builds a
// process, runs PAX, or performs a Bake.
public sealed class ProductionOrganizationWiringTests
{
    // =========================================================================
    // TEST-FIRST anchor. Production organization readiness no longer receives
    // null inventory / catalog / clock dependencies.
    // =========================================================================
    [Fact]
    public void TF01_ProductionCookPath_NoLongerInjectsNullOrganizationDependencies()
    {
        string cookStart = ReadSource("src/PAXCookbook.App/RecipeReadModel.CookStart.cs");

        foreach (string nullSeam in new[] { "Evaluation: null", "Catalog: null", "Clock: null" })
        {
            Assert.DoesNotContain(nullSeam, cookStart, StringComparison.Ordinal);
        }

        Assert.Contains(
            "ProductionOrganizationAuthority.CreateSnapshot(versionInfo, engine)",
            cookStart,
            StringComparison.Ordinal);
    }

    // =========================================================================
    // matrix 1 — the factory is built from the machine-policy source and the
    // other REAL production sources, with no caller/env/HTTP override and no
    // cross-call caching.
    // =========================================================================
    [Fact]
    public void W01_Factory_UsesTheMachinePolicySourceAndTheRealProductionChain()
    {
        string src = ReadSource(AuthorityFile);

        foreach (string required in new[]
        {
            "MachinePolicyParser.Classify(new MachinePolicyRegistrySource().Read())",
            "ManagedChefKeysGate.Evaluate(detection)",
            "OrganizationKeyInventoryEvaluator.EvaluateDetailed(",
            "new ProgramDataOrganizationKeyInventorySource()",
            "new OnceQueriedCertificateCatalog(new MachineCertificateCatalog())",
            "new SystemCertificateUsabilityClock()",
        })
        {
            Assert.Contains(required, src, StringComparison.Ordinal);
        }

        // No override seam of any kind, and no cross-call authority cache.
        string code = StripComments(src);
        foreach (string forbidden in new[]
        {
            "GetEnvironmentVariable", "HttpClient", "HttpContext", "Lazy<", "MemoryCache",
            "_cached", "??=", "static readonly LocalOrganizationAuthority",
        })
        {
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("string path", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ICertificateCatalog catalog", code, StringComparison.Ordinal);

        // Live, read-only: the factory answers with real instances, and two
        // calls never share authority state.
        LocalOrganizationAuthority first = ProductionOrganizationAuthority.CreateLocalAuthority();
        LocalOrganizationAuthority second = ProductionOrganizationAuthority.CreateLocalAuthority();

        Assert.NotNull(first.Evaluation);
        Assert.NotNull(first.Catalog);
        Assert.NotNull(first.Clock);
        Assert.NotSame(first, second);
        Assert.NotSame(first.Evaluation, second.Evaluation);
        Assert.NotSame(first.Catalog, second.Catalog);
        Assert.NotSame(first.Clock, second.Clock);
    }

    // =========================================================================
    // matrix 2 — a DENIED policy short-circuits the inventory source, the
    // certificate catalog, AND the acquired-engine capability record.
    // =========================================================================
    [Fact]
    public void W02_PolicyDenied_ShortCircuitsInventoryCatalogAndCapability()
    {
        var source = new CountingSource(OrganizationInventorySourceResult.Provided(ReadyDoc));
        var catalog = new CountingCatalog(ReadyCatalog());
        var capability = new CountingCapability(EngineCapabilityState.Available);

        OrganizationInventoryEvaluation denied =
            OrganizationKeyInventoryEvaluator.EvaluateDetailed(DeniedGate(), source);

        PaxAdapter.ChefKeyAuthRow? row = RecipeReadModel.TryPrepareOrganizationAuthRow(
            OrganizationRecipe(),
            OrgKeyId,
            new RecipeReadModel.OrganizationCookPreparationContext(
                capability.Read, denied, catalog, OrganizationKeyNotYetRunnableTests.Clock()));

        Assert.Null(row);
        Assert.Equal(0, source.Loads);
        Assert.Equal(0, catalog.Queries);
        Assert.Equal(0, capability.Reads);
    }

    // =========================================================================
    // matrix 3 — an AUTHORIZED policy actually reads the trusted inventory.
    // =========================================================================
    [Fact]
    public void W03_AuthorizedPolicy_ReadsTheTrustedInventory()
    {
        var source = new CountingSource(OrganizationInventorySourceResult.Provided(ReadyDoc));
        OrganizationInventoryEvaluation evaluation =
            OrganizationKeyInventoryEvaluator.EvaluateDetailed(AuthorizedGate(), source);

        Assert.Equal(1, source.Loads);
        Assert.True(evaluation.Projection.InventoryLoaded);
        Assert.Equal(OrgKeyId, Assert.Single(evaluation.Entries).OrganizationKeyId);
    }

    // =========================================================================
    // matrix 4 — an absent / unavailable / untrusted / invalid inventory all
    // fail closed.
    // =========================================================================
    [Fact]
    public void W04_InventoryAbsentUnavailableUntrustedInvalid_AllFailClosed()
    {
        foreach (OrganizationInventorySourceResult result in new[]
        {
            OrganizationInventorySourceResult.NotProvisioned(),
            OrganizationInventorySourceResult.Unavailable(),
            OrganizationInventorySourceResult.Untrusted(),
            OrganizationInventorySourceResult.InvalidContent(),
            OrganizationInventorySourceResult.Provided("{ not json"),
        })
        {
            AssertRefused(OrganizationKeyInventoryEvaluator.EvaluateDetailed(
                AuthorizedGate(), new CountingSource(result)));
        }

        // A null evaluation is the same refusal.
        AssertRefused(evaluation: null);
    }

    // =========================================================================
    // matrix 5 — a missing, duplicate, or administrator-disabled entry fails
    // closed.
    // =========================================================================
    [Fact]
    public void W05_EntryMissingDuplicateOrDisabled_FailsClosed()
    {
        // Missing: a provisioned inventory that lists a DIFFERENT key.
        AssertRefused(Provisioned(Doc(V2Entry("contoso.other_key", FingerprintB))));

        // Disabled: the bound entry exists but the administrator turned it off.
        AssertRefused(Provisioned(Doc(V2Entry(OrgKeyId, FingerprintA, adminState: "disabled"))));

        // Duplicate: the parser rejects the whole document, so the entry is
        // unrepresentable AND the binding still fails closed.
        OrganizationInventoryEvaluation duplicate = Provisioned(Doc(
            V2Entry(OrgKeyId, FingerprintA), V2Entry(OrgKeyId, FingerprintB)));
        Assert.False(duplicate.Projection.InventoryLoaded);
        AssertRefused(duplicate);
    }

    // =========================================================================
    // matrix 6 — a missing reference, a not-found certificate, and an ambiguous
    // certificate all fail closed.
    // =========================================================================
    [Fact]
    public void W06_ReferenceMissingNotFoundOrAmbiguous_FailsClosed()
    {
        // Reference missing: a schema-v1 entry carries no certificate reference.
        AssertRefused(Provisioned(V1Doc(OrgKeyId)));

        // Not found: the catalog holds a different certificate entirely.
        AssertRefused(
            ReadyEvaluation(),
            OrganizationKeyNotYetRunnableTests.CatalogWith(
                (FingerprintB, OrganizationKeyNotYetRunnableTests.UsableFacts())));

        // Ambiguous: two occurrences carry the same reference.
        AssertRefused(
            ReadyEvaluation(),
            OrganizationKeyNotYetRunnableTests.CatalogWith(
                (FingerprintA, OrganizationKeyNotYetRunnableTests.UsableFacts()),
                (FingerprintA, OrganizationKeyNotYetRunnableTests.UsableFacts())));
    }

    // =========================================================================
    // matrix 7 — an unavailable catalog fails closed.
    // =========================================================================
    [Fact]
    public void W07_CatalogUnavailable_FailsClosed()
    {
        AssertRefused(
            ReadyEvaluation(),
            new SyntheticCertificateCatalog(CertificateCatalogResult.Unavailable()));

        // A null catalog is the same refusal.
        Assert.Null(RecipeReadModel.TryPrepareOrganizationAuthRow(
            OrganizationRecipe(),
            OrgKeyId,
            new RecipeReadModel.OrganizationCookPreparationContext(
                () => EngineCapabilityState.Available,
                ReadyEvaluation(),
                null,
                OrganizationKeyNotYetRunnableTests.Clock())));
    }

    // =========================================================================
    // matrix 8 — EVERY bounded usability failure fails closed.
    // =========================================================================
    [Fact]
    public void W08_EveryUsabilityFailure_FailsClosed()
    {
        foreach (CertificateUsabilityFacts facts in new[]
        {
            OrganizationKeyNotYetRunnableTests.ExpiredFacts(),
            Facts(notBefore: Now.AddDays(5), notAfter: Now.AddDays(400)),
            Facts(purposeExtensionPresent: false),
            Facts(purposeOids: new[] { "1.3.6.1.5.5.7.3.1" }),
            Facts(keyUsageExtensionPresent: false),
            Facts(digitalSignatureAllowed: false),
            Facts(algorithm: CertificateKeyAlgorithmClass.Unsupported),
            Facts(keyAvailability: CertificateKeyAvailability.Unavailable),
        })
        {
            AssertRefused(
                ReadyEvaluation(),
                OrganizationKeyNotYetRunnableTests.CatalogWith((FingerprintA, facts)));
        }

        // A certificate the catalog could not describe at all is also refused.
        AssertRefused(
            ReadyEvaluation(),
            OrganizationKeyNotYetRunnableTests.CatalogWith((FingerprintA, null)));
    }

    // =========================================================================
    // matrix 9 — a LOCALLY USABLE binding on the CURRENT engine is
    // `organization_key_not_yet_runnable`, verbatim.
    // =========================================================================
    [Fact]
    public void W09_LocallyUsable_WithCurrentEngine_IsNotYetRunnable()
    {
        // The local binding really is ready...
        OrganizationKeyBindingReadiness? readiness = RecipeReadinessModel.ProjectOrganizationKeyBinding(
            OrganizationRecipe(), ReadyEvaluation(), ReadyCatalog(),
            OrganizationKeyNotYetRunnableTests.Clock());
        Assert.NotNull(readiness);
        Assert.True(readiness!.Ready);
        Assert.Equal("organization_key_ready", readiness.WireState);

        // ...and every state the CURRENT engine can produce still refuses.
        foreach (EngineCapabilityState state in IncapableStates)
        {
            (int status, object? body, PaxAdapter.ChefKeyAuthRow? row) = CookSeam(state);

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

    // =========================================================================
    // matrix 10 — a LOCALLY USABLE binding on a SYNTHETIC capable engine
    // reaches the locally runnable preparation state.
    // =========================================================================
    [Fact]
    public void W10_LocallyUsable_WithSyntheticCapableEngine_IsPreparationReady()
    {
        (int status, object? body, PaxAdapter.ChefKeyAuthRow? row) =
            CookSeam(EngineCapabilityState.Available);

        Assert.Equal(200, status);
        Assert.Null(body);
        Assert.NotNull(row);
        Assert.Equal("AppRegistrationCertificate", row!.Mode);
        Assert.Equal(FingerprintA, row.CertSha256);
        Assert.Null(row.CertThumbprint);
    }

    // =========================================================================
    // matrix 11 — preview and readiness consume the PRODUCTION coordinator.
    // =========================================================================
    [Fact]
    public void W11_PreviewAndReadiness_ConsumeTheProductionCoordinator()
    {
        string preview = ReadSource("src/PAXCookbook.App/RecipePreviewModel.cs");
        string readiness = ReadSource("src/PAXCookbook.App/RecipeReadinessModel.cs");
        string program = ReadSource(ProgramFile);

        // The projection REQUIRES an authority (no defaulted null seam) and
        // reuses the ONE bounded preparation decision.
        Assert.Contains(
            "Func<RecipeReadModel.OrganizationCookPreparationContext>? organizationAuthority)",
            preview, StringComparison.Ordinal);
        Assert.DoesNotContain("organizationAuthority = null", preview, StringComparison.Ordinal);
        Assert.Contains("RecipeReadModel.TryPrepareOrganizationAuthRow(", preview, StringComparison.Ordinal);

        // Both production entry points supply the real factory.
        Assert.Contains(
            "ProductionOrganizationAuthority.CreateSnapshot(versionInfo, engine)",
            readiness, StringComparison.Ordinal);
        Assert.Contains("ProductionOrganizationAuthority.CreateSnapshot(", program, StringComparison.Ordinal);
        Assert.Contains(
            "ProductionOrganizationAuthority.CreateLocalAuthority()", program, StringComparison.Ordinal);

        // Behaviourally: the coordinator's verdict is what preview obeys.
        Assert.False(Preview(Authority(EngineCapabilityState.NotDeclared)).Ok);
        Assert.True(Preview(Authority(EngineCapabilityState.Available)).Ok);
    }

    // =========================================================================
    // matrix 12 — Cook re-evaluates independently: the authority is a FACTORY
    // invoked per call, never a shared or memoized value.
    // =========================================================================
    [Fact]
    public void W12_CookReEvaluates_FromAFreshSnapshotEveryCall()
    {
        string cookStart = ReadSource(CookStartFile);
        Assert.Contains(
            "Func<OrganizationCookPreparationContext>? organizationAuthority = null",
            cookStart, StringComparison.Ordinal);
        Assert.Contains("organizationAuthority?.Invoke()", cookStart, StringComparison.Ordinal);
        Assert.Contains(
            "Func<OrganizationCookPreparationContext> organizationPreparation =",
            cookStart, StringComparison.Ordinal);

        int invocations = 0;
        Func<RecipeReadModel.OrganizationCookPreparationContext> counting = () =>
        {
            invocations++;
            return Authority(EngineCapabilityState.NotDeclared)();
        };

        RecipePreviewModel.Project(Workspace, EnginePath, Version(), OrganizationRecipe(), counting);
        Assert.Equal(1, invocations);
        RecipePreviewModel.Project(Workspace, EnginePath, Version(), OrganizationRecipe(), counting);
        Assert.Equal(2, invocations);

        // A PERSONAL Recipe never touches the organization authority at all.
        RecipePreviewModel.Project(Workspace, EnginePath, Version(), PersonalRecipe(), counting);
        Assert.Equal(2, invocations);
    }

    // =========================================================================
    // matrix 13 — state that changed between preview and Cook blocks the Cook.
    // =========================================================================
    [Fact]
    public void W13_StateChangedAfterPreview_StillBlocksTheCook()
    {
        // Preview saw a ready binding and a capable engine.
        Assert.True(Preview(Authority(EngineCapabilityState.Available)).Ok);

        // The administrator then disabled the entry; Cook re-reads and refuses.
        (int disabledStatus, _, PaxAdapter.ChefKeyAuthRow? disabledRow) =
            RecipeReadModel.TestSeamResolveOrganizationCookPreparation(
                OrgKeyId,
                EngineCapabilityState.Available,
                Provisioned(Doc(V2Entry(OrgKeyId, FingerprintA, adminState: "disabled"))),
                ReadyCatalog(),
                OrganizationKeyNotYetRunnableTests.Clock());
        Assert.Equal(412, disabledStatus);
        Assert.Null(disabledRow);

        // Or the engine lost the capability; Cook refuses on that alone.
        (int engineStatus, _, PaxAdapter.ChefKeyAuthRow? engineRow) =
            CookSeam(EngineCapabilityState.EngineHashMismatch);
        Assert.Equal(412, engineStatus);
        Assert.Null(engineRow);
    }

    // =========================================================================
    // matrix 14 — the UI selector's `eligible` flag is never execution
    // authority.
    // =========================================================================
    [Fact]
    public void W14_SelectorEligibility_IsNeverConsumedAsAuthority()
    {
        IReadOnlyList<OrganizationKeySelectorEntry> selectable =
            OrganizationKeySelectorProjection.Build(
                ReadyEvaluation(), ReadyCatalog(), OrganizationKeyNotYetRunnableTests.Clock());

        OrganizationKeySelectorEntry entry = Assert.Single(selectable);
        Assert.True(entry.Eligible);

        // Eligible in the picker, still refused by the pre-Cook gate.
        foreach (EngineCapabilityState state in IncapableStates)
        {
            (int status, _, PaxAdapter.ChefKeyAuthRow? row) = CookSeam(state);
            Assert.Equal(412, status);
            Assert.Null(row);
        }

        // The gate never reads a selector projection.
        string cookStart = ReadSource(CookStartFile);
        Assert.DoesNotContain("OrganizationKeySelectorProjection", cookStart, StringComparison.Ordinal);
        Assert.DoesNotContain("Eligible", cookStart, StringComparison.Ordinal);
    }

    // =========================================================================
    // matrix 15 — never a fallback to the personal key.
    // =========================================================================
    [Fact]
    public void W15_NeverFallsBackToThePersonalKey()
    {
        Dictionary<string, object?> both = RecipeWithAuth($$"""
        {
          "mode": "AppRegistrationCertificate",
          "tenantId": "{{TenantId}}",
          "chefKeyId": "some-personal-key",
          "organizationKeyId": "{{OrgKeyId}}"
        }
        """);

        // Even with a fully ready inventory AND a capable engine, a Recipe that
        // also carries a personal key is refused rather than downgraded.
        Assert.Null(RecipeReadModel.TryPrepareOrganizationAuthRow(
            both, OrgKeyId, Authority(EngineCapabilityState.Available)()));

        RecipePreviewModel.ProjectionResult projection = RecipePreviewModel.Project(
            Workspace, EnginePath, Version(), both, Authority(EngineCapabilityState.Available));
        Assert.False(projection.Ok);
        Assert.Null(projection.Plan);
        Assert.Null(projection.AuthRow);

        string json = JsonSerializer.Serialize(projection.ErrorBody);
        // The refusal is the EARLIER, stronger mutual-exclusion rule: a Recipe
        // may reference a personal key OR an organization key, never both.
        Assert.Contains("organizationKeyBindingConflict", json, StringComparison.Ordinal);
        // It never resolves, names, or falls back to the personal key.
        Assert.DoesNotContain("some-personal-key", json, StringComparison.Ordinal);
        Assert.DoesNotContain("chefKeyNotFound", json, StringComparison.Ordinal);
    }

    // =========================================================================
    // matrix 16 — the CURRENT engine never produces a SHA-256 selector argv.
    // =========================================================================
    [Fact]
    public void W16_CurrentEngine_NeverEmitsTheSha256Selector()
    {
        foreach (EngineCapabilityState state in IncapableStates)
        {
            (_, object? body, PaxAdapter.ChefKeyAuthRow? row) = CookSeam(state);
            Assert.Null(row);

            string json = JsonSerializer.Serialize(body);
            Assert.DoesNotContain("ClientCertificateSha256", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ClientCertificateThumbprint", json, StringComparison.OrdinalIgnoreCase);
        }

        RecipePreviewModel.ProjectionResult projection =
            Preview(Authority(EngineCapabilityState.NotDeclared));
        Assert.False(projection.Ok);
        Assert.Null(projection.Plan);
        Assert.DoesNotContain(
            "ClientCertificate",
            JsonSerializer.Serialize(projection.ErrorBody),
            StringComparison.OrdinalIgnoreCase);
    }

    // =========================================================================
    // matrix 17 — a SYNTHETIC capable engine emits the exact new parameter.
    // =========================================================================
    [Fact]
    public void W17_SyntheticCapableEngine_EmitsExactlyTheNewParameter()
    {
        (_, _, PaxAdapter.ChefKeyAuthRow? row) = CookSeam(EngineCapabilityState.Available);
        Assert.NotNull(row);

        List<string> argv = PaxAdapter.GetArgvArray(OrganizationRecipe(), row, "local-manual");

        int selector = argv.IndexOf("-ClientCertificateSha256");
        Assert.True(selector >= 0, "the SHA-256 selector was not emitted");
        Assert.Equal(FingerprintA, argv[selector + 1]);
        Assert.Equal(1, argv.Count(a => string.Equals(a, "-ClientCertificateSha256", StringComparison.Ordinal)));

        Assert.DoesNotContain("-ClientCertificateThumbprint", argv);
        Assert.DoesNotContain("-ClientCertificatePath", argv);
        Assert.DoesNotContain("-ClientCertificatePassword", argv);

        string joined = string.Join(" ", argv);
        Assert.DoesNotContain("CurrentUser", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OrgKeyId, joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Contoso Managed Key", joined, StringComparison.Ordinal);
    }

    // =========================================================================
    // matrix 18 — no opaque identifier, display name, or certificate reference
    // in the preview, the refusal, or any bounded readiness surface.
    // =========================================================================
    [Fact]
    public void W18_NoIdentifierReachesPreviewOrAnyBoundedSurface()
    {
        var surfaces = new List<string>();

        foreach (EngineCapabilityState state in IncapableStates)
        {
            (_, object? body, _) = CookSeam(state);
            surfaces.Add(JsonSerializer.Serialize(body));
        }

        surfaces.Add(JsonSerializer.Serialize(
            Preview(Authority(EngineCapabilityState.NotDeclared)).ErrorBody));

        OrganizationKeyBindingReadiness? readiness = RecipeReadinessModel.ProjectOrganizationKeyBinding(
            OrganizationRecipe(), ReadyEvaluation(), ReadyCatalog(),
            OrganizationKeyNotYetRunnableTests.Clock());
        surfaces.Add(readiness!.ToString());
        surfaces.Add(readiness.WireState);
        surfaces.Add(readiness.Detail);

        foreach (string surface in surfaces)
        {
            foreach (string forbidden in new[]
            {
                OrgKeyId, FingerprintA, "Contoso Managed Key", "client-ref-1", "tenant-ref-1",
                "humbprint", "SHA-256", "SHA256", "LocalMachine", "CurrentUser",
            })
            {
                Assert.DoesNotContain(forbidden, surface, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // =========================================================================
    // matrix 19 — no process construction anywhere in the wiring.
    // =========================================================================
    [Fact]
    public void W19_WiringConstructsNoProcessAndInvokesNoEngine()
    {
        foreach (string relative in new[] { AuthorityFile, "src/PAXCookbook.App/RecipePreviewModel.cs" })
        {
            string code = StripComments(ReadSource(relative));
            foreach (string forbidden in new[]
            {
                "new Process(", "Process.Start(", "ProcessStartInfo", "Diagnostics.Process",
                "pwsh", "PAX_Purview", "SpawnAndSupervise", "X509Store", "X509Certificate",
                "PrivateKey", "WindowsCredentialStore", "HttpClient", "graph.microsoft.com",
                "GraphServiceClient",
            })
            {
                Assert.DoesNotContain(forbidden, code, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The shared preparation decision constructs nothing either.
        string cookStart = ReadSource(CookStartFile);
        int helper = cookStart.IndexOf(
            "internal static PaxAdapter.ChefKeyAuthRow? TryPrepareOrganizationAuthRow",
            StringComparison.Ordinal);
        Assert.True(helper > 0, "the shared preparation helper is missing");
        string helperBody = cookStart.Substring(helper);
        Assert.True(helperBody.Length > 200, "the helper body must be scannable");
        foreach (string forbidden in new[]
        {
            "new Process(", "Process.Start(", "ProcessStartInfo", "X509Store", "SHA1", "Sha1",
        })
        {
            Assert.DoesNotContain(forbidden, helperBody, StringComparison.Ordinal);
        }
    }

    // =========================================================================
    // matrix 20 — the PERSONAL readiness / Cook path is unchanged.
    // =========================================================================
    [Fact]
    public void W20_PersonalPath_IsUnchanged()
    {
        // Personal preview keeps its pre-existing bounded error.
        RecipePreviewModel.ProjectionResult projection = RecipePreviewModel.Project(
            Workspace, EnginePath, Version(), PersonalRecipe(),
            Authority(EngineCapabilityState.Available));
        Assert.False(projection.Ok);
        string json = JsonSerializer.Serialize(projection.ErrorBody);
        Assert.Contains("chefKeyNotFound", json, StringComparison.Ordinal);
        Assert.DoesNotContain("organization_key_not_yet_runnable", json, StringComparison.Ordinal);

        // Interactive / empty modes still fall straight through gate 14.
        foreach (string mode in new[] { "WebLogin", "DeviceCode", "" })
        {
            (int status, object? body, bool hasRow) =
                RecipeReadModel.TestSeamResolveAuthForProjection(mode, null, null);
            Assert.Equal(200, status);
            Assert.Null(body);
            Assert.False(hasRow);
        }

        // A personal App-registration recipe keeps its own bounded error.
        (int appStatus, object? appBody, bool appRow) =
            RecipeReadModel.TestSeamResolveAuthForProjection("AppRegistrationCertificate", null, null);
        Assert.Equal(412, appStatus);
        Assert.False(appRow);
        Assert.Contains("no chefKeyId is set", JsonSerializer.Serialize(appBody), StringComparison.Ordinal);
    }

    // =========================================================================
    // Exactly ONE production wiring location for the whole chain.
    // =========================================================================
    [Fact]
    public void W21_ExactlyOneProductionWiringLocation()
    {
        string[] productionFiles = Directory.GetFiles(
            Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories);

        var chainBuilders = new List<string>();
        foreach (string file in productionFiles)
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }
            string code = StripComments(File.ReadAllText(file));
            if (code.Contains("new ProgramDataOrganizationKeyInventorySource()", StringComparison.Ordinal)
                || code.Contains("new MachineCertificateCatalog()", StringComparison.Ordinal)
                || code.Contains("new SystemCertificateUsabilityClock()", StringComparison.Ordinal)
                || code.Contains("new MachinePolicyRegistrySource()", StringComparison.Ordinal))
            {
                chainBuilders.Add(Path.GetFileName(file));
            }
        }

        Assert.Equal(new[] { "ProductionOrganizationAuthority.cs" }, chainBuilders.ToArray());
    }

    // ---- shared fixtures -----------------------------------------------------

    private const string Workspace = "C:\\PAXCookbookTestWorkspace";
    private const string EnginePath = "C:\\PAXCookbookTestEngine\\pax.ps1";
    private const string AuthorityFile = "src/PAXCookbook.App/ProductionOrganizationAuthority.cs";
    private const string CookStartFile = "src/PAXCookbook.App/RecipeReadModel.CookStart.cs";
    private const string ProgramFile = "src/PAXCookbook.App/Program.cs";

    private const string ValidUlid = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string OrgKeyId = OrganizationKeyNotYetRunnableTests.OrgKeyId;
    private const string FingerprintA = OrganizationKeyNotYetRunnableTests.FingerprintA;
    private const string FingerprintB = OrganizationKeyNotYetRunnableTests.FingerprintB;

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly EngineCapabilityState[] IncapableStates =
    {
        EngineCapabilityState.NotDeclared,
        EngineCapabilityState.EngineNotAcquired,
        EngineCapabilityState.EngineHashMismatch,
        EngineCapabilityState.EngineVersionMismatch,
        EngineCapabilityState.StateInvalid,
        EngineCapabilityState.Unknown,
    };

    private static readonly string ReadyDoc = Doc(V2Entry(OrgKeyId, FingerprintA));

    private static ManagedChefKeysGateProjection AuthorizedGate()
        => ManagedChefKeysGate.Evaluate(
            MachinePolicyDetection.ConfiguredOrganizationManaged(
                MachinePolicyCapability.Enabled, MachinePolicyCapability.Disabled));

    private static ManagedChefKeysGateProjection DeniedGate()
        => ManagedChefKeysGate.Evaluate(
            MachinePolicyDetection.ConfiguredOrganizationManaged(
                MachinePolicyCapability.Disabled, MachinePolicyCapability.Disabled));

    private static string V2Entry(string id, string fingerprint, string adminState = "enabled")
        => OrganizationKeyNotYetRunnableTests.V2Entry(id, fingerprint, adminState);

    private static string V1Doc(string id)
        => "{ \"schemaVersion\": 1, \"entries\": [{ \"entryVersion\": 1"
           + ", \"organizationKeyId\": \"" + id + "\""
           + ", \"displayName\": \"Contoso Managed Key\""
           + ", \"certificateReferenceType\": \"app_registration_certificate\""
           + ", \"adminState\": \"enabled\""
           + ", \"tenantReference\": \"tenant-ref-1\""
           + ", \"clientReference\": \"client-ref-1\" }] }";

    private static string Doc(params string[] entries)
        => OrganizationKeyNotYetRunnableTests.Doc(2, entries);

    private static OrganizationInventoryEvaluation Provisioned(string document)
        => OrganizationKeyNotYetRunnableTests.Provisioned(document);

    private static OrganizationInventoryEvaluation ReadyEvaluation() => Provisioned(ReadyDoc);

    private static ICertificateCatalog ReadyCatalog()
        => OrganizationKeyNotYetRunnableTests.CatalogWith(
            (FingerprintA, OrganizationKeyNotYetRunnableTests.UsableFacts()));

    private static CertificateUsabilityFacts Facts(
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        CertificateKeyAlgorithmClass algorithm = CertificateKeyAlgorithmClass.Rsa,
        CertificateKeyAvailability keyAvailability = CertificateKeyAvailability.Available,
        bool purposeExtensionPresent = true,
        string[]? purposeOids = null,
        bool keyUsageExtensionPresent = true,
        bool digitalSignatureAllowed = true)
        => CertificateUsabilityFacts.Create(
            notBefore ?? Now.AddDays(-1),
            notAfter ?? Now.AddDays(30),
            algorithm,
            keyAvailability,
            purposeExtensionPresent,
            purposeOids ?? new[] { OrganizationCertificateUsabilityContract.ClientAuthenticationPurposeOid },
            keyUsageExtensionPresent,
            digitalSignatureAllowed);

    private static Func<RecipeReadModel.OrganizationCookPreparationContext> Authority(
        EngineCapabilityState capability)
        => () => new RecipeReadModel.OrganizationCookPreparationContext(
            () => capability, ReadyEvaluation(), ReadyCatalog(),
            OrganizationKeyNotYetRunnableTests.Clock());

    private static (int Status, object? Body, PaxAdapter.ChefKeyAuthRow? Row) CookSeam(
        EngineCapabilityState capability)
        => RecipeReadModel.TestSeamResolveOrganizationCookPreparation(
            OrgKeyId, capability, ReadyEvaluation(), ReadyCatalog(),
            OrganizationKeyNotYetRunnableTests.Clock());

    private static RecipePreviewModel.ProjectionResult Preview(
        Func<RecipeReadModel.OrganizationCookPreparationContext> authority)
        => RecipePreviewModel.Project(Workspace, EnginePath, Version(), OrganizationRecipe(), authority);

    // A refusal proven through the SHARED production preparation decision, with
    // a CAPABLE engine so ONLY the local chain can be responsible.
    private static void AssertRefused(
        OrganizationInventoryEvaluation? evaluation, ICertificateCatalog? catalog = null)
    {
        Assert.Null(RecipeReadModel.TryPrepareOrganizationAuthRow(
            OrganizationRecipe(),
            OrgKeyId,
            new RecipeReadModel.OrganizationCookPreparationContext(
                () => EngineCapabilityState.Available,
                evaluation,
                catalog ?? ReadyCatalog(),
                OrganizationKeyNotYetRunnableTests.Clock())));
    }

    private sealed class CountingSource : IOrganizationKeyInventorySource
    {
        private readonly OrganizationInventorySourceResult _result;

        internal CountingSource(OrganizationInventorySourceResult result) { _result = result; }

        internal int Loads { get; private set; }

        public OrganizationInventorySourceResult Load()
        {
            Loads++;
            return _result;
        }
    }

    private sealed class CountingCatalog : ICertificateCatalog
    {
        private readonly ICertificateCatalog _inner;

        internal CountingCatalog(ICertificateCatalog inner) { _inner = inner; }

        internal int Queries { get; private set; }

        public CertificateCatalogResult Query()
        {
            Queries++;
            return _inner.Query();
        }
    }

    private sealed class CountingCapability
    {
        private readonly EngineCapabilityState _state;

        internal CountingCapability(EngineCapabilityState state) { _state = state; }

        internal int Reads { get; private set; }

        internal EngineCapabilityState Read()
        {
            Reads++;
            return _state;
        }
    }

    // ---- recipe builders -----------------------------------------------------

    private static Dictionary<string, object?> RecipeWithAuth(string authJson)
    {
        string json = $$"""
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
        """;
        using var doc = JsonDocument.Parse(json);
        return BuildObject(doc.RootElement);
    }

    private static Dictionary<string, object?> OrganizationRecipe() => RecipeWithAuth($$"""
    {
      "mode": "AppRegistrationCertificate",
      "tenantId": "{{TenantId}}",
      "organizationKeyId": "{{OrgKeyId}}"
    }
    """);

    private static Dictionary<string, object?> PersonalRecipe() => RecipeWithAuth($$"""
    {
      "mode": "AppRegistrationCertificate",
      "tenantId": "{{TenantId}}",
      "chefKeyId": "no-such-personal-key"
    }
    """);

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
}
