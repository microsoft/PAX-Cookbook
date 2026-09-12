using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle-11 focused matrix for the APP-LINKED CERTIFICATE CATALOG and the GATED
// resolution coordinator.
//
// Nothing here opens, enumerates, reads, installs, deletes, or mutates a REAL
// certificate store, touches a key pair, reads ProgramData or the registry,
// contacts a tenant, Graph, a service, WAM/Hello, PAX, or a Bake. The production
// catalog is exercised ONLY through its internal encoded-bytes test seam with
// synthetic bytes; the developer's real machine catalog is never probed.
// Containment is proven by deterministic comment/string-stripped source scans
// over the NEW catalog file only.
public sealed class OrganizationCertificateCatalogTests
{
    // ---- seams ---------------------------------------------------------------

    // A catalog that FAILS the test if it is ever consulted. It counts every call
    // and throws, so a missing gate short-circuit cannot pass silently.
    private sealed class NeverQueriedCatalog : ICertificateCatalog
    {
        public int QueryCount { get; private set; }

        public CertificateCatalogResult Query()
        {
            QueryCount++;
            throw new InvalidOperationException(
                "The certificate catalog must not be consulted for this inventory state.");
        }
    }

    // Counts how many times the catalog was asked to answer, so "exactly one
    // query for the whole batch" is observable.
    private sealed class CountingCatalog : ICertificateCatalog
    {
        private readonly ICertificateCatalog _inner;

        public CountingCatalog(ICertificateCatalog inner)
        {
            _inner = inner;
        }

        public int QueryCount { get; private set; }

        public CertificateCatalogResult Query()
        {
            QueryCount++;
            return _inner.Query();
        }
    }

    private sealed class InMemoryOrganizationKeyInventorySource : IOrganizationKeyInventorySource
    {
        private readonly OrganizationInventorySourceResult _result;

        public InMemoryOrganizationKeyInventorySource(OrganizationInventorySourceResult result)
        {
            _result = result;
        }

        public OrganizationInventorySourceResult Load() => _result;
    }

    // ---- synthetic encoded bytes (NEVER a real certificate) ------------------

    private static readonly byte[] DerA = { 0x30, 0x03, 0x01, 0x41 };
    private static readonly byte[] DerB = { 0x30, 0x03, 0x01, 0x42 };
    private static readonly byte[] DerC = { 0x30, 0x03, 0x01, 0x43 };
    private static readonly byte[] DerD = { 0x30, 0x03, 0x01, 0x44 };

    private static string Fingerprint(byte[] der) => Convert.ToHexString(SHA256.HashData(der));

    private const string ClientAuthOid =
        OrganizationCertificateUsabilityContract.ClientAuthenticationPurposeOid;

    // A SYNTHETIC certificate built entirely IN MEMORY. It is never installed,
    // never written to disk, and never placed in any certificate store, and the
    // developer's real machine catalog is never opened.
    private static X509Certificate2 Synthetic(
        bool ecdsa = false,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        string[]? purposeOids = null,
        X509KeyUsageFlags? keyUsage = X509KeyUsageFlags.DigitalSignature)
    {
        CertificateRequest request;
        if (ecdsa)
        {
            ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            request = new CertificateRequest(
                "CN=PAX Cookbook Synthetic", key, HashAlgorithmName.SHA256);
        }
        else
        {
            RSA key = RSA.Create(2048);
            request = new CertificateRequest(
                "CN=PAX Cookbook Synthetic", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }

        if (purposeOids is not null)
        {
            var oids = new OidCollection();
            foreach (string oid in purposeOids)
            {
                oids.Add(new Oid(oid));
            }
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(oids, false));
        }
        if (keyUsage is not null)
        {
            request.CertificateExtensions.Add(new X509KeyUsageExtension(keyUsage.Value, false));
        }

        return request.CreateSelfSigned(
            notBefore ?? DateTimeOffset.UtcNow.AddDays(-1),
            notAfter ?? DateTimeOffset.UtcNow.AddDays(30));
    }

    // The production catalog is driven ONLY through its internal test seam, with
    // fresh copies so the catalog's own byte-clearing cannot corrupt the fixtures.
    private static MachineCertificateCatalog Catalog(params byte[][] ders)
        => new(() => ders.Select(d => (byte[])d.Clone()).ToArray());

    // ---- document builders ---------------------------------------------------

    private static string Doc(int schemaVersion, params string[] entries)
        => "{ \"schemaVersion\": " + schemaVersion + ", \"entries\": [" + string.Join(",", entries) + "] }";

    private static string Common(string id, string adminState)
        => ", \"organizationKeyId\": \"" + id + "\""
           + ", \"displayName\": \"Contoso Managed Key\""
           + ", \"certificateReferenceType\": \"app_registration_certificate\""
           + ", \"adminState\": \"" + adminState + "\""
           + ", \"tenantReference\": \"tenant-ref-1\""
           + ", \"clientReference\": \"client-ref-1\"";

    private static string V2Entry(string id, byte[] der, string adminState = "enabled")
        => "{ \"entryVersion\": 2" + Common(id, adminState)
           + ", \"certificateSha256\": \"" + Fingerprint(der).ToLowerInvariant() + "\" }";

    private static string V1Entry(string id, string adminState = "enabled")
        => "{ \"entryVersion\": 1" + Common(id, adminState) + " }";

    // ---- gates and evaluations ----------------------------------------------

    private static ManagedChefKeysGateProjection AuthorizedGate()
        => ManagedChefKeysGate.Evaluate(
            MachinePolicyDetection.ConfiguredOrganizationManaged(
                MachinePolicyCapability.Enabled, MachinePolicyCapability.Disabled));

    private static ManagedChefKeysGateProjection DeniedNotConfiguredGate()
        => ManagedChefKeysGate.Evaluate(MachinePolicyDetection.NotConfigured());

    private static OrganizationInventoryEvaluation Provisioned(string document)
        => OrganizationKeyInventoryEvaluator.EvaluateDetailed(
            AuthorizedGate(),
            new InMemoryOrganizationKeyInventorySource(
                OrganizationInventorySourceResult.Provided(document)));

    private static OrganizationInventoryEvaluation FromSource(OrganizationInventorySourceResult result)
        => OrganizationKeyInventoryEvaluator.EvaluateDetailed(
            AuthorizedGate(), new InMemoryOrganizationKeyInventorySource(result));

    // ==== A. Gate short-circuits (the catalog is never opened) ================

    [Fact] // C01 — an unauthorized inventory never opens the catalog.
    public void C01_UnauthorizedInventory_NeverOpensCatalog()
    {
        var catalog = new NeverQueriedCatalog();

        OrganizationInventoryEvaluation evaluation = OrganizationKeyInventoryEvaluator.EvaluateDetailed(
            DeniedNotConfiguredGate(), new NotProvisionedOrganizationKeyInventorySource());

        OrganizationCertificateAggregate aggregate =
            OrganizationCertificateResolutionCoordinator.Resolve(evaluation, catalog);

        Assert.Equal(0, catalog.QueryCount);
        Assert.False(evaluation.Projection.InventoryLoaded);
        Assert.Empty(evaluation.Entries);
        AssertNoResolution(aggregate);
    }

    [Fact] // C02 — an authorized but NOT-PROVISIONED inventory never opens the catalog.
    public void C02_NotProvisionedInventory_NeverOpensCatalog()
    {
        var catalog = new NeverQueriedCatalog();

        OrganizationInventoryEvaluation evaluation = OrganizationKeyInventoryEvaluator.EvaluateDetailed(
            AuthorizedGate(), new NotProvisionedOrganizationKeyInventorySource());

        OrganizationCertificateAggregate aggregate =
            OrganizationCertificateResolutionCoordinator.Resolve(evaluation, catalog);

        Assert.Equal(OrganizationInventoryState.AuthorizedNotProvisioned, evaluation.Projection.State);
        Assert.Equal(0, catalog.QueryCount);
        Assert.Empty(evaluation.Entries);
        AssertNoResolution(aggregate);
    }

    [Fact] // C03 — unavailable / untrusted / invalid inventories never open the catalog.
    public void C03_UnavailableUntrustedInvalidInventory_NeverOpensCatalog()
    {
        OrganizationInventorySourceResult[] sources =
        {
            OrganizationInventorySourceResult.Unavailable(),
            OrganizationInventorySourceResult.Untrusted(),
            OrganizationInventorySourceResult.InvalidContent(),
            OrganizationInventorySourceResult.Provided("{ not json"),
        };

        foreach (OrganizationInventorySourceResult source in sources)
        {
            var catalog = new NeverQueriedCatalog();
            OrganizationInventoryEvaluation evaluation = FromSource(source);

            OrganizationCertificateAggregate aggregate =
                OrganizationCertificateResolutionCoordinator.Resolve(evaluation, catalog);

            Assert.False(evaluation.Projection.InventoryLoaded);
            Assert.Equal(0, catalog.QueryCount);
            Assert.Empty(evaluation.Entries);
            AssertNoResolution(aggregate);
        }

        // A null evaluation is the same fail-closed shape.
        var nullCatalog = new NeverQueriedCatalog();
        AssertNoResolution(OrganizationCertificateResolutionCoordinator.Resolve(null, nullCatalog));
        Assert.Equal(0, nullCatalog.QueryCount);
    }

    [Fact] // C04 — a provisioned inventory whose only entry is disabled never opens the catalog.
    public void C04_DisabledOnlyInventory_ResolvesWithoutCatalogAccess()
    {
        var catalog = new NeverQueriedCatalog();
        OrganizationInventoryEvaluation evaluation =
            Provisioned(Doc(2, V2Entry("org-key-1", DerA, adminState: "disabled")));

        OrganizationCertificateAggregate aggregate =
            OrganizationCertificateResolutionCoordinator.Resolve(evaluation, catalog);

        Assert.True(evaluation.Projection.InventoryLoaded);
        Assert.Equal(0, catalog.QueryCount);
        Assert.Equal(1, aggregate.DisabledCount);
        Assert.Equal(0, aggregate.ResolvedMetadataCount);
        Assert.False(aggregate.CatalogUnavailable);
    }

    // ==== B. Fixed, read-only catalog source ==================================

    [Fact] // C05 — the catalog source is fixed to the machine catalog and nothing else.
    public void C05_CatalogSource_IsFixedMachineCatalogOnly()
    {
        string code = CatalogCode;
        Assert.Contains("StoreLocation.LocalMachine", code);
        Assert.Contains("StoreName.My", code);

        // No other location/name, and no runtime-injected selection of either.
        Assert.DoesNotContain("CurrentUser", code);
        Assert.DoesNotContain("StoreName.Root", code);
        Assert.DoesNotContain("StoreName.CertificateAuthority", code);
        Assert.DoesNotContain("StoreName.TrustedPeople", code);
        Assert.DoesNotContain("StoreName.TrustedPublisher", code);
        Assert.DoesNotContain("GetEnvironmentVariable", code);
        Assert.DoesNotContain("GetCommandLineArgs", code);
        Assert.DoesNotContain("Registry", code);
        Assert.DoesNotContain("HttpContext", code);
        Assert.DoesNotContain("HttpRequest", code);
        Assert.DoesNotContain("HttpClient", code);
        Assert.DoesNotContain("TestIsolation", code);

        // The production constructor takes no source selector at all.
        ConstructorInfo? production = typeof(MachineCertificateCatalog).GetConstructor(Type.EmptyTypes);
        Assert.NotNull(production);
        Assert.True(production!.IsPublic);

        // The only other constructors are the INTERNAL test seams: the Cycle-11
        // byte enumerator, and the Cycle-12 overload that also substitutes the
        // private-key AVAILABILITY probe so "declares a key but the accessor
        // yields nothing" and "the accessor throws" can be proven without a key.
        ConstructorInfo[] all = typeof(MachineCertificateCatalog).GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Equal(3, all.Length);
        ConstructorInfo seam = all.Single(c => c.GetParameters().Length == 1);
        Assert.False(seam.IsPublic);
        Assert.Equal(typeof(Func<IReadOnlyList<byte[]>?>), seam.GetParameters()[0].ParameterType);

        ConstructorInfo probeSeam = all.Single(c => c.GetParameters().Length == 2);
        Assert.False(probeSeam.IsPublic);
        Assert.Equal(typeof(Func<IReadOnlyList<byte[]>?>), probeSeam.GetParameters()[0].ParameterType);
        Assert.Equal(
            typeof(Func<X509Certificate2, CertificateKeyAvailability>),
            probeSeam.GetParameters()[1].ParameterType);
    }

    [Fact] // C06 — the catalog opens read-only and existing-only, and never creates a store.
    public void C06_CatalogOpen_IsReadOnlyExistingOnly()
    {
        string code = CatalogCode;
        Assert.Contains("OpenFlags.OpenExistingOnly", code);
        Assert.Contains("OpenFlags.ReadOnly", code);
        Assert.DoesNotContain("ReadWrite", code);
        Assert.DoesNotContain("MaxAllowed", code);
        Assert.DoesNotContain("IncludeArchived", code);
    }

    // ==== C. Fail-closed catalog behavior =====================================

    [Fact] // C07 — an unavailable store yields an unavailable catalog, not a partial one.
    public void C07_StoreUnavailable_CatalogUnavailable()
    {
        var catalog = new MachineCertificateCatalog(
            () => throw new InvalidOperationException("store unavailable"));

        CertificateCatalogResult result = catalog.Query();

        Assert.Equal(CertificateCatalogStatus.Unavailable, result.Status);
        Assert.Empty(result.Occurrences);
    }

    [Fact] // C08 — an enumeration failure yields an unavailable catalog.
    public void C08_EnumerationFailure_CatalogUnavailable()
    {
        Assert.Equal(
            CertificateCatalogStatus.Unavailable,
            new MachineCertificateCatalog(() => null).Query().Status);

        // Above the bounded maximum the catalog fails closed rather than truncating.
        byte[][] tooMany = Enumerable
            .Range(0, OrganizationCertificateResolutionContract.MaxCatalogOccurrences + 1)
            .Select(i => new byte[] { 0x30, 0x02, (byte)i })
            .ToArray();
        Assert.Equal(
            CertificateCatalogStatus.Unavailable,
            Catalog(tooMany).Query().Status);
    }

    [Fact] // C09 — one failed byte retrieval fails the WHOLE catalog, never partially.
    public void C09_EncodedRetrievalFailure_FailsWholeCatalog()
    {
        var withNull = new MachineCertificateCatalog(
            () => new byte[]?[] { (byte[])DerA.Clone(), null, (byte[])DerB.Clone() }!);

        CertificateCatalogResult result = withNull.Query();

        Assert.Equal(CertificateCatalogStatus.Unavailable, result.Status);
        Assert.Empty(result.Occurrences);
    }

    // ==== D. Fingerprint normalization and occurrence shape ===================

    [Fact] // C10 — the occurrence fingerprint is the uppercase SHA-256 over the DER bytes.
    public void C10_Fingerprint_IsUppercaseSha256OverEncodedBytes()
    {
        CertificateCatalogResult result = Catalog(DerA, DerB).Query();

        Assert.Equal(CertificateCatalogStatus.Available, result.Status);
        Assert.Equal(2, result.Occurrences.Count);
        Assert.Equal(Fingerprint(DerA), result.Occurrences[0].Fingerprint);
        Assert.Equal(Fingerprint(DerB), result.Occurrences[1].Fingerprint);
        foreach (CertificateCatalogOccurrence occurrence in result.Occurrences)
        {
            Assert.Equal(64, occurrence.Fingerprint.Length);
            Assert.Equal(occurrence.Fingerprint.ToUpperInvariant(), occurrence.Fingerprint);
        }
        Assert.Equal(0, result.Occurrences[0].Ordinal);
        Assert.Equal(1, result.Occurrences[1].Ordinal);
    }

    // ==== E. Per-entry resolution through the coordinator =====================

    [Fact] // C11 — exactly one occurrence resolves to METADATA ONLY.
    public void C11_SingleOccurrence_ResolvesMetadataOnly()
    {
        var catalog = new CountingCatalog(Catalog(DerA, DerB));
        OrganizationCertificateAggregate aggregate = OrganizationCertificateResolutionCoordinator.Resolve(
            Provisioned(Doc(2, V2Entry("org-key-1", DerA))), catalog);

        Assert.Equal(1, catalog.QueryCount);
        Assert.Equal(1, aggregate.ResolvedMetadataCount);
        Assert.Equal(0, aggregate.NotFoundCount);
        Assert.Equal(0, aggregate.AmbiguousCount);
        Assert.False(aggregate.CatalogUnavailable);
    }

    [Fact] // C12 — zero occurrences is NOT FOUND, never a match.
    public void C12_ZeroOccurrences_NotFound()
    {
        OrganizationCertificateAggregate aggregate = OrganizationCertificateResolutionCoordinator.Resolve(
            Provisioned(Doc(2, V2Entry("org-key-1", DerA))), Catalog(DerB));

        Assert.Equal(0, aggregate.ResolvedMetadataCount);
        Assert.Equal(1, aggregate.NotFoundCount);
        Assert.False(aggregate.CatalogUnavailable);
    }

    [Fact] // C13 — duplicate occurrences are AMBIGUOUS (retained, never de-duplicated).
    public void C13_DuplicateOccurrences_Ambiguous()
    {
        CertificateCatalogResult raw = Catalog(DerA, DerA).Query();
        Assert.Equal(2, raw.Occurrences.Count);
        Assert.Equal(raw.Occurrences[0].Fingerprint, raw.Occurrences[1].Fingerprint);

        OrganizationCertificateAggregate aggregate = OrganizationCertificateResolutionCoordinator.Resolve(
            Provisioned(Doc(2, V2Entry("org-key-1", DerA))), Catalog(DerA, DerA));

        Assert.Equal(0, aggregate.ResolvedMetadataCount);
        Assert.Equal(1, aggregate.AmbiguousCount);
    }

    [Fact] // C14 — a schema-v1 entry has no reference at all.
    public void C14_SchemaOneEntry_ReferenceMissing_NeverOpensCatalog()
    {
        var catalog = new NeverQueriedCatalog();
        OrganizationCertificateAggregate aggregate = OrganizationCertificateResolutionCoordinator.Resolve(
            Provisioned(Doc(1, V1Entry("org-key-1"))), catalog);

        Assert.Equal(0, catalog.QueryCount);
        Assert.Equal(1, aggregate.ReferenceMissingCount);
        Assert.Equal(0, aggregate.ResolvedMetadataCount);
        Assert.False(aggregate.CatalogUnavailable);
    }

    [Fact] // C15 — a disabled entry counts as disabled and is never looked up.
    public void C15_DisabledEntry_CountsDisabled()
    {
        OrganizationCertificateAggregate aggregate = OrganizationCertificateResolutionCoordinator.Resolve(
            Provisioned(Doc(2, V2Entry("org-key-1", DerA), V2Entry("org-key-2", DerB, adminState: "disabled"))),
            Catalog(DerA, DerB));

        // The disabled entry's reference IS in the catalog, yet it stays disabled.
        Assert.Equal(1, aggregate.DisabledCount);
        Assert.Equal(1, aggregate.ResolvedMetadataCount);
        Assert.Equal(0, aggregate.NotFoundCount);
    }

    [Fact] // C16 — the aggregate tallies every bucket exactly once.
    public void C16_AggregateCounts_AreCorrect()
    {
        var catalog = new CountingCatalog(Catalog(DerA, DerB, DerB));
        OrganizationCertificateAggregate aggregate = OrganizationCertificateResolutionCoordinator.Resolve(
            Provisioned(Doc(2,
                V2Entry("org-key-1", DerA),
                V2Entry("org-key-2", DerB),
                V2Entry("org-key-3", DerC),
                V2Entry("org-key-4", DerD, adminState: "disabled"))),
            catalog);

        // ONE query answers the whole batch.
        Assert.Equal(1, catalog.QueryCount);
        Assert.Equal(1, aggregate.ResolvedMetadataCount);
        Assert.Equal(1, aggregate.AmbiguousCount);
        Assert.Equal(1, aggregate.NotFoundCount);
        Assert.Equal(1, aggregate.DisabledCount);
        Assert.Equal(0, aggregate.ReferenceMissingCount);
        Assert.False(aggregate.CatalogUnavailable);

        // A schema-v1 document tallies the remaining bucket.
        OrganizationCertificateAggregate v1 = OrganizationCertificateResolutionCoordinator.Resolve(
            Provisioned(Doc(1, V1Entry("org-key-1"), V1Entry("org-key-2", adminState: "disabled"))),
            Catalog(DerA));
        Assert.Equal(1, v1.ReferenceMissingCount);
        Assert.Equal(1, v1.DisabledCount);

        // A non-answering catalog fails closed with zero resolved/not-found/ambiguous.
        OrganizationCertificateAggregate unavailable = OrganizationCertificateResolutionCoordinator.Resolve(
            Provisioned(Doc(2, V2Entry("org-key-1", DerA))),
            new MachineCertificateCatalog(() => null));
        Assert.True(unavailable.CatalogUnavailable);
        Assert.Equal(0, unavailable.ResolvedMetadataCount);
        Assert.Equal(0, unavailable.NotFoundCount);
        Assert.Equal(0, unavailable.AmbiguousCount);

        // A null catalog is the same fail-closed shape, with no query at all.
        OrganizationCertificateAggregate nullCatalog = OrganizationCertificateResolutionCoordinator.Resolve(
            Provisioned(Doc(2, V2Entry("org-key-1", DerA))), null);
        Assert.True(nullCatalog.CatalogUnavailable);
        Assert.Equal(0, nullCatalog.ResolvedMetadataCount);
    }

    // ==== F. Bounded projection (counts only, never identity) =================

    [Fact] // C17 — the aggregate projects counts only: no identifier, no reference.
    public void C17_AggregateProjection_CarriesNoIdentifier()
    {
        // Cycle 12 narrowed the bare tokens `client`, `tenant`, and `store` to
        // IDENTIFIER-SPECIFIC tokens. `ClientAuthNotAllowedCount` denotes the TLS
        // Client Authentication EKU POLICY STATE, not a client identifier, and
        // this scan must keep catching a real identifier while allowing a policy
        // name. Every identifier-VALUE assertion below is unchanged.
        string[] forbidden =
        {
            "fingerprint", "subject", "issuer", "serial", "thumbprint", "sha256",
            "clientreference", "clientid", "tenantreference", "tenantid",
            "storename", "storepath", "path",
        };
        foreach (MemberInfo member in typeof(OrganizationCertificateAggregate).GetMembers(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            foreach (string token in forbidden)
            {
                Assert.DoesNotContain(token, member.Name, StringComparison.OrdinalIgnoreCase);
            }
        }

        OrganizationCertificateAggregate aggregate = OrganizationCertificateResolutionCoordinator.Resolve(
            Provisioned(Doc(2, V2Entry("org-key-1", DerA))), Catalog(DerA));
        string text = aggregate.ToString();
        Assert.Equal("OrganizationCertificateAggregate[metadata_only]", text);
        Assert.DoesNotContain(Fingerprint(DerA), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("org-key-1", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant-ref-1", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("client-ref-1", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact] // C18 — no certificate bytes cross the catalog boundary.
    public void C18_NoCertificateBytes_CrossTheBoundary()
    {
        byte[] supplied = (byte[])DerA.Clone();
        var catalog = new MachineCertificateCatalog(() => new[] { supplied });

        CertificateCatalogResult result = catalog.Query();

        Assert.Equal(CertificateCatalogStatus.Available, result.Status);
        // The transient encoded bytes are cleared once hashed.
        Assert.All(supplied, b => Assert.Equal(0, b));

        // Nothing on the boundary types can carry bytes or a handle.
        foreach (Type type in new[]
                 {
                     typeof(CertificateCatalogOccurrence),
                     typeof(CertificateCatalogResult),
                     typeof(OrganizationCertificateAggregate),
                     typeof(CertificateUsabilityFacts),
                     typeof(CertificateUsability),
                 })
        {
            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                Assert.NotEqual(typeof(byte[]), property.PropertyType);
                Assert.NotEqual(typeof(IntPtr), property.PropertyType);
                Assert.DoesNotContain("X509", property.PropertyType.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("RSA", property.PropertyType.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("ECDsa", property.PropertyType.Name, StringComparison.Ordinal);
            }
        }
    }

    [Fact] // C19 — no other certificate field is read or projected.
    public void C19_NoOtherCertificateField_IsReadOrProjected()
    {
        // Cycle 12 REQUIRES the validity window and the extension collection, so
        // NotBefore, NotAfter, and Extensions are now allowed. Every OTHER field
        // ban is unchanged, and no timestamp or extension value is ever projected.
        string code = CatalogCode;
        Assert.DoesNotContain("Thumbprint", code);
        Assert.DoesNotContain("Subject", code);
        Assert.DoesNotContain("Issuer", code);
        Assert.DoesNotContain("SerialNumber", code);
        Assert.DoesNotContain("FriendlyName", code);
        Assert.DoesNotContain("Verify", code);
        Assert.DoesNotContain("X509Chain", code);
        Assert.DoesNotContain("RevocationMode", code);
        Assert.DoesNotContain("ChainPolicy", code);

        // The organization wire object carries none of them either. The scan is
        // scoped to organizationKeys: the PRE-EXISTING personal chefKeys array
        // legitimately carries its own certificate reference field and is out of
        // scope for this cycle.
        (JsonElement org, _) = Wire(
            OrganizationInventoryProjection.AuthorizedProvisioned(1),
            OrganizationCertificateResolutionCoordinator.Resolve(
                Provisioned(Doc(2, V2Entry("org-key-1", DerA))), Catalog(DerA)));

        string organizationJson = org.GetRawText();
        foreach (string token in new[]
                 {
                     "thumbprint", "subject", "issuer", "serial", "fingerprint",
                     "sha256", "org-key-1", "tenant-ref-1", "client-ref-1",
                 })
        {
            Assert.DoesNotContain(token, organizationJson, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(1, org.GetProperty("resolvedMetadataCount").GetInt32());
    }

    // ==== G. Prohibited-surface containment (NEW catalog file only) ===========

    [Fact] // C20 — the catalog reads private-key AVAILABILITY and NEVER USES a key.
    public void C20_Catalog_TouchesNoKeyPair()
    {
        // Cycle 12 REQUIRES exactly four availability APIs: HasPrivateKey,
        // GetRSAPrivateKey, GetECDsaPrivateKey, and the PrivateKey-family naming
        // they share. Every USE of a key stays banned, and the returned key object
        // is disposed immediately (proven by the `using` assertions below).
        string code = CatalogCode;
        Assert.Contains("HasPrivateKey", code);
        Assert.Contains("GetRSAPrivateKey", code);
        Assert.Contains("GetECDsaPrivateKey", code);
        Assert.Contains("using RSA? rsa = certificate.GetRSAPrivateKey();", code);
        Assert.Contains("using ECDsa? ecdsa = certificate.GetECDsaPrivateKey();", code);

        Assert.DoesNotContain("GetDSAPrivateKey", code);
        Assert.DoesNotContain("CopyWithPrivateKey", code);
        Assert.DoesNotContain("SignData", code);
        Assert.DoesNotContain("SignHash", code);
        Assert.DoesNotContain("VerifyData", code);
        Assert.DoesNotContain("VerifyHash", code);
        Assert.DoesNotContain("Encrypt", code);
        Assert.DoesNotContain("Decrypt", code);
        Assert.DoesNotContain("ExportParameters", code);
        Assert.DoesNotContain("ExportPkcs8", code);
        Assert.DoesNotContain("ImportPkcs8", code);
        Assert.DoesNotContain("CspParameters", code);
        Assert.DoesNotContain("CngKey", code);
        Assert.DoesNotContain("KeyContainer", code);
        Assert.DoesNotContain("KeyName", code);
        Assert.DoesNotContain("ProviderName", code);
        Assert.DoesNotContain("SHA1", code);
    }

    [Fact] // C21 — the catalog mutates nothing and reaches no activation surface.
    public void C21_Catalog_MutatesNothing_AndReachesNoActivationSurface()
    {
        string code = CatalogCode;
        Assert.DoesNotContain(".Add(", code);
        Assert.DoesNotContain(".Remove(", code);
        Assert.DoesNotContain(".Find(", code);
        Assert.DoesNotContain("Import", code);
        Assert.DoesNotContain("Export", code);
        Assert.DoesNotContain("AddRange", code);
        Assert.DoesNotContain("RemoveRange", code);
        Assert.DoesNotContain("CertificateRequest", code);

        // No service / Graph / auth / PAX / Bake / Cook integration.
        Assert.DoesNotContain("GraphServiceClient", code);
        Assert.DoesNotContain("AcquireToken", code);
        Assert.DoesNotContain("PublicClientApplication", code);
        Assert.DoesNotContain("Microsoft.Identity", code);
        Assert.DoesNotContain("WebAuthn", code);
        Assert.DoesNotContain("ServiceController", code);
        Assert.DoesNotContain("Process.Start", code);
        Assert.DoesNotContain("PaxAdapter", code);
        Assert.DoesNotContain("WindowsCredentialStore", code);
        Assert.DoesNotContain("File.", code);
        Assert.DoesNotContain("Directory.", code);
    }

    // ==== H. Capabilities and non-regression =================================

    [Fact] // C22 — resolution NEVER grants a capability, however many entries resolved.
    public void C22_Aggregate_GrantsNoCapability()
    {
        foreach (OrganizationCertificateAggregate aggregate in new[]
                 {
                     OrganizationCertificateAggregate.None(),
                     OrganizationCertificateResolutionCoordinator.Resolve(
                         Provisioned(Doc(2, V2Entry("org-key-1", DerA))), Catalog(DerA)),
                 })
        {
            Assert.False(aggregate.Usable);
            Assert.False(aggregate.PrivateKeyAvailable);
            Assert.False(aggregate.RecipeBound);
            Assert.False(aggregate.BakeAuthorized);
            Assert.False(aggregate.ServiceReady);
            Assert.True(aggregate.ReadOnly);
            Assert.True(aggregate.MetadataOnly);
        }
    }

    [Fact] // C23 — the personal Chef's Keys array is identical across overloads.
    public void C23_PersonalChefKeys_Unchanged()
    {
        OrganizationInventoryProjection projection =
            OrganizationInventoryProjection.AuthorizedNotProvisioned();

        (int legacyStatus, object legacyBody) = ChefKeyModel.List(projection);
        (int status, object body) = ChefKeyModel.List(projection, OrganizationCertificateAggregate.None());

        Assert.Equal(legacyStatus, status);
        Assert.Equal(Personal(legacyBody), Personal(body));

        // The non-provisioned wire object is unchanged too: no count is emitted.
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        JsonElement org = doc.RootElement.GetProperty("organizationKeys");
        Assert.False(org.TryGetProperty("entryCount", out _));
        Assert.False(org.TryGetProperty("resolvedMetadataCount", out _));
        Assert.False(org.TryGetProperty("catalogUnavailable", out _));
    }

    [Fact] // C24 — the existing single-projection overload survives unchanged.
    public void C24_ExistingOverloads_Preserved()
    {
        MethodInfo[] overloads = typeof(ChefKeyModel)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "List")
            .ToArray();

        // Cycle 14s adds ONE additive overload (the bounded organization selector
        // projection). The three pre-existing overloads survive unchanged.
        Assert.Equal(4, overloads.Length);
        Assert.Single(overloads, m => m.GetParameters().Length == 1
            && m.GetParameters()[0].ParameterType == typeof(ManagedChefKeysGateProjection));
        Assert.Single(overloads, m => m.GetParameters().Length == 1
            && m.GetParameters()[0].ParameterType == typeof(OrganizationInventoryProjection));
        Assert.Single(overloads, m => m.GetParameters().Length == 2
            && m.GetParameters()[0].ParameterType == typeof(OrganizationInventoryProjection)
            && m.GetParameters()[1].ParameterType == typeof(OrganizationCertificateAggregate));
        Assert.Single(overloads, m => m.GetParameters().Length == 3
            && m.GetParameters()[0].ParameterType == typeof(OrganizationInventoryProjection)
            && m.GetParameters()[1].ParameterType == typeof(OrganizationCertificateAggregate));

        // The detailed evaluator is a pure superset: the projection it returns is
        // identical to what the existing Evaluate produces for the same inputs.
        var source = new InMemoryOrganizationKeyInventorySource(
            OrganizationInventorySourceResult.Provided(Doc(2, V2Entry("org-key-1", DerA))));
        Assert.Equal(
            OrganizationKeyInventoryEvaluator.Evaluate(AuthorizedGate(), source).ToString(),
            OrganizationKeyInventoryEvaluator.EvaluateDetailed(AuthorizedGate(), source).Projection.ToString());
    }

    // ==== I. Bounded usability validation (Cycle 12) ==========================

    [Fact] // C25 — the catalog extracts BOUNDED usability facts, and unparseable
           // bytes fail closed to an unavailable fact record without dropping the
           // occurrence.
    public void C25_Catalog_ExtractsBoundedUsabilityFacts()
    {
        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-2);
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.AddDays(20);
        using X509Certificate2 certificate = Synthetic(
            notBefore: notBefore, notAfter: notAfter, purposeOids: new[] { ClientAuthOid });

        CertificateCatalogResult result = Catalog(certificate.RawData).Query();

        Assert.Equal(CertificateCatalogStatus.Available, result.Status);
        CertificateUsabilityFacts facts = Assert.IsType<CertificateUsabilityFacts>(
            result.Occurrences[0].Usability);
        Assert.False(facts.ExtractionFailed);
        Assert.True(facts.PurposeExtensionPresent);
        Assert.False(facts.PurposeExtensionMalformed);
        Assert.Contains(ClientAuthOid, facts.PurposeOids);
        Assert.True(facts.KeyUsageExtensionPresent);
        Assert.True(facts.DigitalSignatureAllowed);
        Assert.Equal(CertificateKeyAlgorithmClass.Rsa, facts.KeyAlgorithm);
        Assert.True((facts.NotValidBeforeUtc - notBefore).Duration() < TimeSpan.FromSeconds(2));
        Assert.True((facts.NotValidAfterUtc - notAfter).Duration() < TimeSpan.FromSeconds(2));

        // The synthetic seam re-parses PUBLIC bytes only, so no key exists there.
        Assert.Equal(CertificateKeyAvailability.Unavailable, facts.KeyAvailability);

        // Unparseable bytes still produce an occurrence, with unusable facts.
        CertificateCatalogResult unparseable = Catalog(DerA).Query();
        Assert.Equal(CertificateCatalogStatus.Available, unparseable.Status);
        Assert.True(unparseable.Occurrences[0].Usability!.ExtractionFailed);
    }

    [Fact] // C26 — the REAL availability probe, exercised against an in-memory key
           // that never touches the machine catalog. Matrix 18 / 19 / 20.
    public void C26_RealAvailabilityProbe_ReportsAvailabilityOnly()
    {
        using X509Certificate2 rsa = Synthetic(purposeOids: new[] { ClientAuthOid });
        CertificateUsabilityFacts rsaFacts = MachineCertificateCatalog.ExtractFacts(rsa, null);
        Assert.Equal(CertificateKeyAlgorithmClass.Rsa, rsaFacts.KeyAlgorithm);
        Assert.Equal(CertificateKeyAvailability.Available, rsaFacts.KeyAvailability);

        using X509Certificate2 ecdsa = Synthetic(ecdsa: true, purposeOids: new[] { ClientAuthOid });
        CertificateUsabilityFacts ecdsaFacts = MachineCertificateCatalog.ExtractFacts(ecdsa, null);
        Assert.Equal(CertificateKeyAlgorithmClass.Ecdsa, ecdsaFacts.KeyAlgorithm);
        Assert.Equal(CertificateKeyAvailability.Available, ecdsaFacts.KeyAvailability);

        // The PUBLIC-only encoding declares no private key at all.
        using var publicOnly = new X509Certificate2(rsa.RawData);
        Assert.False(publicOnly.HasPrivateKey);
        Assert.Equal(
            CertificateKeyAvailability.Unavailable,
            MachineCertificateCatalog.ExtractFacts(publicOnly, null).KeyAvailability);

        // Availability is the ONLY thing recorded: nothing names the key.
        foreach (PropertyInfo property in typeof(CertificateUsabilityFacts).GetProperties(
            BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.DoesNotContain("container", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("provider", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("keyname", property.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact] // C27 — a declared-but-unobtainable key and a THROWING accessor both fail
           // closed, with no exception detail escaping. Matrix 21 / 22.
    public void C27_KeyProbe_FailsClosed()
    {
        using X509Certificate2 certificate = Synthetic(purposeOids: new[] { ClientAuthOid });

        // Declares a key, but the supported accessor yields nothing.
        Assert.Equal(
            CertificateKeyAvailability.Unavailable,
            MachineCertificateCatalog.ExtractFacts(
                certificate, _ => CertificateKeyAvailability.Unavailable).KeyAvailability);

        // The accessor throws.
        Assert.Equal(
            CertificateKeyAvailability.Unavailable,
            MachineCertificateCatalog.ExtractFacts(
                certificate, _ => throw new CryptographicException("probe denied")).KeyAvailability);

        // The same through the catalog's probe seam.
        byte[] der = certificate.RawData;
        var throwing = new MachineCertificateCatalog(
            () => new[] { (byte[])der.Clone() },
            _ => throw new UnauthorizedAccessException("probe denied"));
        CertificateCatalogResult result = throwing.Query();
        Assert.Equal(CertificateCatalogStatus.Available, result.Status);
        Assert.Equal(
            CertificateKeyAvailability.Unavailable,
            result.Occurrences[0].Usability!.KeyAvailability);
    }

    [Fact] // C28 — the coordinator evaluates usability ONLY for a unique match and
           // tallies every bounded outcome exactly once. Matrix 24 / 25.
    public void C28_Coordinator_TalliesUsabilityForUniqueMatchesOnly()
    {
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FixedCertificateUsabilityClock(now);

        using X509Certificate2 usable = Synthetic(
            notBefore: now.AddDays(-1), notAfter: now.AddDays(30),
            purposeOids: new[] { ClientAuthOid });
        using X509Certificate2 expired = Synthetic(
            notBefore: now.AddDays(-30), notAfter: now.AddDays(-1),
            purposeOids: new[] { ClientAuthOid });
        using X509Certificate2 noPurpose = Synthetic(
            notBefore: now.AddDays(-1), notAfter: now.AddDays(30), purposeOids: null);
        using X509Certificate2 noKeyUsage = Synthetic(
            notBefore: now.AddDays(-1), notAfter: now.AddDays(30),
            purposeOids: new[] { ClientAuthOid }, keyUsage: null);

        byte[] a = usable.RawData;
        byte[] b = expired.RawData;
        byte[] c = noPurpose.RawData;
        byte[] d = noKeyUsage.RawData;

        var catalog = new MachineCertificateCatalog(
            () => new[] { (byte[])a.Clone(), (byte[])b.Clone(), (byte[])c.Clone(), (byte[])d.Clone() },
            _ => CertificateKeyAvailability.Available);

        OrganizationCertificateAggregate aggregate =
            OrganizationCertificateResolutionCoordinator.Resolve(
                Provisioned(Doc(2,
                    V2Entry("org-key-1", a),
                    V2Entry("org-key-2", b),
                    V2Entry("org-key-3", c),
                    V2Entry("org-key-4", d))),
                catalog,
                clock);

        Assert.Equal(4, aggregate.ResolvedMetadataCount);
        Assert.Equal(1, aggregate.UsableCount);
        Assert.Equal(1, aggregate.ExpiredCount);
        Assert.Equal(1, aggregate.ClientAuthNotAllowedCount);
        Assert.Equal(1, aggregate.DigitalSignatureNotAllowedCount);
        Assert.Equal(0, aggregate.NotYetValidCount);
        Assert.Equal(0, aggregate.UnsupportedKeyAlgorithmCount);
        Assert.Equal(0, aggregate.PrivateKeyUnavailableCount);
        Assert.Equal(0, aggregate.UsabilityInvalidCount);
        Assert.False(aggregate.UsabilityUnavailable);

        // A NOT-FOUND, AMBIGUOUS, DISABLED, or schema-v1 entry is never evaluated.
        OrganizationCertificateAggregate skipped =
            OrganizationCertificateResolutionCoordinator.Resolve(
                Provisioned(Doc(2,
                    V2Entry("org-key-1", DerB),
                    V2Entry("org-key-2", a, adminState: "disabled"))),
                new MachineCertificateCatalog(
                    () => new[] { (byte[])a.Clone(), (byte[])a.Clone() },
                    _ => CertificateKeyAvailability.Available),
                clock);
        Assert.Equal(1, skipped.NotFoundCount);
        Assert.Equal(1, skipped.DisabledCount);
        Assert.Equal(0, skipped.UsableCount);
        Assert.False(skipped.UsabilityUnavailable);

        // A missing clock is a REFUSAL, never an assumption of usability.
        OrganizationCertificateAggregate noClock =
            OrganizationCertificateResolutionCoordinator.Resolve(
                Provisioned(Doc(2, V2Entry("org-key-1", a))),
                new MachineCertificateCatalog(
                    () => new[] { (byte[])a.Clone() },
                    _ => CertificateKeyAvailability.Available));
        Assert.Equal(1, noClock.ResolvedMetadataCount);
        Assert.Equal(0, noClock.UsableCount);
        Assert.True(noClock.UsabilityUnavailable);
    }

    [Fact] // C29 — the usability counts reach the wire, and STILL carry no
           // identifier, no timestamp, and no certificate field. Matrix 26.
    public void C29_UsabilityCounts_ProjectCountsOnly()
    {
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        using X509Certificate2 usable = Synthetic(
            notBefore: now.AddDays(-1), notAfter: now.AddDays(30),
            purposeOids: new[] { ClientAuthOid });
        byte[] der = usable.RawData;

        OrganizationCertificateAggregate aggregate =
            OrganizationCertificateResolutionCoordinator.Resolve(
                Provisioned(Doc(2, V2Entry("org-key-1", der))),
                new MachineCertificateCatalog(
                    () => new[] { (byte[])der.Clone() },
                    _ => CertificateKeyAvailability.Available),
                new FixedCertificateUsabilityClock(now));
        Assert.Equal(1, aggregate.UsableCount);

        (JsonElement org, _) = Wire(
            OrganizationInventoryProjection.AuthorizedProvisioned(1), aggregate);
        string organizationJson = org.GetRawText();

        Assert.Equal(1, org.GetProperty("usableCount").GetInt32());
        Assert.Equal(0, org.GetProperty("expiredCount").GetInt32());
        Assert.Equal(0, org.GetProperty("clientAuthNotAllowedCount").GetInt32());
        Assert.Equal(0, org.GetProperty("digitalSignatureNotAllowedCount").GetInt32());
        Assert.Equal(0, org.GetProperty("privateKeyUnavailableCount").GetInt32());
        Assert.False(org.GetProperty("usabilityUnavailable").GetBoolean());

        foreach (string token in new[]
                 {
                     "thumbprint", "subject", "issuer", "serial", "fingerprint",
                     "sha256", "org-key-1", "tenant-ref-1", "client-ref-1",
                     "notBefore", "notAfter", "validity", "expiresOn", "oid",
                     "keyName", "provider", "container", "2026", "1.3.6.1",
                 })
        {
            Assert.DoesNotContain(token, organizationJson, StringComparison.OrdinalIgnoreCase);
        }

        // Every value in the organization object is a bounded token, count, or
        // flag - never a structure that could carry certificate data.
        foreach (JsonProperty property in org.EnumerateObject())
        {
            Assert.True(
                property.Value.ValueKind is JsonValueKind.Number
                    or JsonValueKind.True or JsonValueKind.False or JsonValueKind.String,
                property.Name);
        }
    }

    [Fact] // C30 — a USABLE result still grants nothing downstream. Matrix 29/30/31.
    public void C30_UsableAggregate_GrantsNoCapability()
    {
        DateTimeOffset now = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        using X509Certificate2 usable = Synthetic(
            notBefore: now.AddDays(-1), notAfter: now.AddDays(30),
            purposeOids: new[] { ClientAuthOid });
        byte[] der = usable.RawData;

        OrganizationCertificateAggregate aggregate =
            OrganizationCertificateResolutionCoordinator.Resolve(
                Provisioned(Doc(2, V2Entry("org-key-1", der))),
                new MachineCertificateCatalog(
                    () => new[] { (byte[])der.Clone() },
                    _ => CertificateKeyAvailability.Available),
                new FixedCertificateUsabilityClock(now));

        Assert.Equal(1, aggregate.UsableCount);
        Assert.False(aggregate.Usable);
        Assert.False(aggregate.PrivateKeyAvailable);
        Assert.False(aggregate.RecipeBound);
        Assert.False(aggregate.BakeAuthorized);
        Assert.False(aggregate.ServiceReady);
        Assert.True(aggregate.ReadOnly);
        Assert.True(aggregate.MetadataOnly);
        Assert.Equal("OrganizationCertificateAggregate[metadata_only]", aggregate.ToString());
    }

    // ---- helpers -------------------------------------------------------------

    private static void AssertNoResolution(OrganizationCertificateAggregate aggregate)
    {
        Assert.False(aggregate.CatalogUnavailable);
        Assert.Equal(0, aggregate.ResolvedMetadataCount);
        Assert.Equal(0, aggregate.NotFoundCount);
        Assert.Equal(0, aggregate.AmbiguousCount);
        Assert.Equal(0, aggregate.ReferenceMissingCount);
        Assert.Equal(0, aggregate.DisabledCount);
    }

    private static string Personal(object body)
    {
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        return doc.RootElement.GetProperty("chefKeys").GetRawText();
    }

    private static (JsonElement Org, string Json) Wire(
        OrganizationInventoryProjection projection, OrganizationCertificateAggregate aggregate)
    {
        (int status, object body) = ChefKeyModel.List(projection, aggregate);
        Assert.Equal(200, status);
        string json = JsonSerializer.Serialize(body);
        using JsonDocument doc = JsonDocument.Parse(json);
        return (doc.RootElement.GetProperty("organizationKeys").Clone(), json);
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        string dir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // Strip block comments, line comments, AND string/char literals so descriptive
    // doctrine comments cannot false-positive a CODE-pattern scan.
    private static string StripCommentsAndStrings(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        src = Regex.Replace(src, @"//[^\r\n]*", " ");
        src = Regex.Replace(src, "@\"(?:[^\"]|\"\")*\"", " ");
        src = Regex.Replace(src, "\"(?:\\\\.|[^\"\\\\])*\"", " ");
        src = Regex.Replace(src, "'(?:\\\\.|[^'\\\\])*'", " ");
        return src;
    }

    // The scan is scoped to the NEW catalog file ONLY. A repo-wide scan would
    // false-positive on the PRE-EXISTING personal Chef's Key certificate helper,
    // which is out of scope and untouched by this cycle.
    private const string CatalogRel = "src/PAXCookbook.App/MachineCertificateCatalog.cs";

    private static string CatalogCode => StripCommentsAndStrings(ReadSource(CatalogRel));
}
