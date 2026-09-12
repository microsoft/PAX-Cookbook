using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

// Cycle-10 focused matrix for the CERTIFICATE-REFERENCE CONTRACT (schema v2) and
// the PURE RESOLUTION MODEL.
//
// Nothing here opens a certificate store, enumerates or reads a certificate,
// touches a private key, reads ProgramData or the registry, contacts a tenant,
// Graph, a service, WAM/Hello, PAX, or a Bake. The resolver is proven only
// against synthetic in-memory inputs, and containment is proven by a
// deterministic comment/string-stripped source scan over the NEW file.
public sealed class OrganizationCertificateResolutionTests
{
    // Well-formed 64-hex references. Lowercase on the wire so normalization is
    // observable; these are arbitrary digests, not real certificate data.
    private const string RefLowerA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RefUpperA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string RefUpperB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string RefMixedA = "aAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaAaA";

    // ---- document builders ---------------------------------------------------

    private static string Doc(int schemaVersion, string entriesJson)
        => "{ \"schemaVersion\": " + schemaVersion + ", \"entries\": " + entriesJson + " }";

    private static string V1Entry(string id = "org-key-1", string adminState = "enabled", string? certRef = null)
    {
        var sb = new StringBuilder();
        sb.Append("{ \"entryVersion\": 1");
        AppendCommon(sb, id, adminState);
        if (certRef is not null) sb.Append(", \"certificateSha256\": \"").Append(certRef).Append('\"');
        sb.Append(" }");
        return sb.ToString();
    }

    private static string V2Entry(
        string id = "org-key-1",
        string adminState = "enabled",
        string? certRef = RefLowerA,
        bool omitReference = false,
        bool nullReference = false,
        int entryVersion = 2)
    {
        var sb = new StringBuilder();
        sb.Append("{ \"entryVersion\": ").Append(entryVersion);
        AppendCommon(sb, id, adminState);
        if (nullReference)
        {
            sb.Append(", \"certificateSha256\": null");
        }
        else if (!omitReference)
        {
            sb.Append(", \"certificateSha256\": \"").Append(certRef).Append('\"');
        }
        sb.Append(" }");
        return sb.ToString();
    }

    private static void AppendCommon(StringBuilder sb, string id, string adminState)
    {
        sb.Append(", \"organizationKeyId\": \"").Append(id).Append('\"');
        sb.Append(", \"displayName\": \"Contoso Managed Key\"");
        sb.Append(", \"certificateReferenceType\": \"app_registration_certificate\"");
        sb.Append(", \"adminState\": \"").Append(adminState).Append('\"');
        sb.Append(", \"tenantReference\": \"tenant-ref-1\"");
        sb.Append(", \"clientReference\": \"client-ref-1\"");
    }

    private static OrganizationKeyInventoryEntry ParseSingle(string document)
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(document);
        Assert.True(r.IsValid);
        Assert.Equal(1, r.EntryCount);
        return r.Entries[0];
    }

    private static void AssertInvalid(
        OrganizationKeyInventoryParseResult r, OrganizationKeyInventoryInvalidReason expected)
    {
        Assert.False(r.IsValid);
        Assert.Equal(expected, r.Reason);
        Assert.Empty(r.Entries);
    }

    private static OrganizationInventoryProjection Provisioned()
        => OrganizationInventoryProjection.AuthorizedProvisioned(1);

    // ==== A. Schema compatibility and reference parsing =======================

    [Fact] // C01 — a schema-v1 document is unchanged by the v2 addition.
    public void C01_SchemaOne_StillParses_AndCarriesNoReference()
    {
        OrganizationKeyInventoryParseResult r = OrganizationKeyInventoryParser.Parse(Doc(1, "[" + V1Entry() + "]"));
        Assert.True(r.IsValid);
        Assert.Equal(1, r.EntryCount);
        Assert.Equal(1, r.Entries[0].EntryVersion);
        Assert.Equal(string.Empty, r.Entries[0].CertificateSha256);
    }

    [Fact] // C02 — a v1 entry has nothing to resolve.
    public void C02_SchemaOneEntry_Resolves_ReferenceMissing()
    {
        OrganizationKeyInventoryEntry entry = ParseSingle(Doc(1, "[" + V1Entry() + "]"));
        CertificateResolution result = OrganizationCertificateResolver.Resolve(
            Provisioned(), entry, SyntheticCertificateCatalog.WithFingerprints(RefUpperA).Query());

        Assert.Equal(CertificateResolutionState.ReferenceMissing, result.State);
        Assert.Equal("reference_missing", result.WireState);
    }

    [Fact] // C03 — a valid v2 reference parses and normalizes to uppercase.
    public void C03_SchemaTwo_ValidReference_ParsesAndNormalizesUppercase()
    {
        OrganizationKeyInventoryEntry entry = ParseSingle(Doc(2, "[" + V2Entry() + "]"));
        Assert.Equal(2, entry.EntryVersion);
        Assert.Equal(RefUpperA, entry.CertificateSha256);

        OrganizationKeyInventoryEntry mixed = ParseSingle(Doc(2, "[" + V2Entry(certRef: RefMixedA) + "]"));
        Assert.Equal(RefMixedA.ToUpperInvariant(), mixed.CertificateSha256);
    }

    [Fact] // C04 — a schema-v2 entry without a certificate reference is rejected.
    public void C04_SchemaTwo_MissingCertificateReference_Invalid()
    {
        AssertInvalid(
            OrganizationKeyInventoryParser.Parse(Doc(2, "[" + V2Entry(omitReference: true) + "]")),
            OrganizationKeyInventoryInvalidReason.MissingField);

        AssertInvalid(
            OrganizationKeyInventoryParser.Parse(Doc(2, "[" + V2Entry(nullReference: true) + "]")),
            OrganizationKeyInventoryInvalidReason.MissingField);
    }

    [Fact] // C05 — a non-hex reference is rejected.
    public void C05_NonHexReference_Invalid()
    {
        string nonHex = new string('g', 64);
        AssertInvalid(
            OrganizationKeyInventoryParser.Parse(Doc(2, "[" + V2Entry(certRef: nonHex) + "]")),
            OrganizationKeyInventoryInvalidReason.InvalidCertificateReference);

        AssertInvalid(
            OrganizationKeyInventoryParser.Parse(Doc(2, "[" + V2Entry(certRef: "0x" + new string('a', 62)) + "]")),
            OrganizationKeyInventoryInvalidReason.InvalidCertificateReference);
    }

    [Fact] // C06 — a reference of the wrong length is rejected on both sides.
    public void C06_WrongLengthReference_Invalid()
    {
        foreach (string bad in new[] { string.Empty, new string('a', 63), new string('a', 65), new string('a', 40) })
        {
            AssertInvalid(
                OrganizationKeyInventoryParser.Parse(Doc(2, "[" + V2Entry(certRef: bad) + "]")),
                OrganizationKeyInventoryInvalidReason.InvalidCertificateReference);
        }
    }

    [Fact] // C07 — whitespace and separator forms are rejected, not tolerated.
    public void C07_WhitespaceOrSeparatorReference_Invalid()
    {
        string spaced = " " + new string('a', 62) + " ";
        string dashed = new string('a', 32) + "-" + new string('a', 31);
        string colonSeparated = string.Join(":", Chunked2(new string('a', 62)));

        foreach (string bad in new[] { spaced, dashed, new string(' ', 64), colonSeparated })
        {
            AssertInvalid(
                OrganizationKeyInventoryParser.Parse(Doc(2, "[" + V2Entry(certRef: bad) + "]")),
                OrganizationKeyInventoryInvalidReason.InvalidCertificateReference);
        }
    }

    [Fact] // C08 — two entries may not claim the same certificate reference.
    public void C08_DuplicateReference_Invalid()
    {
        string entries = "[" + V2Entry("org-a", certRef: RefLowerA) + "," + V2Entry("org-b", certRef: RefLowerA) + "]";
        AssertInvalid(
            OrganizationKeyInventoryParser.Parse(Doc(2, entries)),
            OrganizationKeyInventoryInvalidReason.DuplicateCertificateReference);
    }

    [Fact] // C09 — a case variant is the SAME reference, so it is still a duplicate.
    public void C09_CaseVariantDuplicateReference_Invalid()
    {
        string entries = "[" + V2Entry("org-a", certRef: RefLowerA) + "," + V2Entry("org-b", certRef: RefUpperA) + "]";
        AssertInvalid(
            OrganizationKeyInventoryParser.Parse(Doc(2, entries)),
            OrganizationKeyInventoryInvalidReason.DuplicateCertificateReference);

        string distinct = "[" + V2Entry("org-a", certRef: RefLowerA) + "," + V2Entry("org-b", certRef: RefUpperB) + "]";
        Assert.True(OrganizationKeyInventoryParser.Parse(Doc(2, distinct)).IsValid);
    }

    [Fact] // C10 — exactly {1, 2} are representable, and versions must agree.
    public void C10_UnknownSchemaOrMismatchedEntryVersion_Invalid()
    {
        AssertInvalid(
            OrganizationKeyInventoryParser.Parse(Doc(3, "[]")),
            OrganizationKeyInventoryInvalidReason.UnsupportedSchema);

        // A v1 entry inside a v2 document.
        AssertInvalid(
            OrganizationKeyInventoryParser.Parse(Doc(2, "[" + V2Entry(entryVersion: 1) + "]")),
            OrganizationKeyInventoryInvalidReason.UnsupportedSchema);

        // A v2 entry inside a v1 document.
        AssertInvalid(
            OrganizationKeyInventoryParser.Parse(Doc(1, "[" + V2Entry() + "]")),
            OrganizationKeyInventoryInvalidReason.UnsupportedSchema);
    }

    [Fact] // C11 — the reference addition weakens no prohibited-key rule.
    public void C11_ProhibitedFields_StillRejected_InBothSchemas()
    {
        string v2WithSecret = "{ \"entryVersion\": 2, \"organizationKeyId\": \"org-key-1\", \"clientSecret\": \"x\" }";
        AssertInvalid(
            OrganizationKeyInventoryParser.Parse(Doc(2, "[" + v2WithSecret + "]")),
            OrganizationKeyInventoryInvalidReason.ProhibitedField);

        string v2WithCertificate = "{ \"entryVersion\": 2, \"certificate\": null }";
        AssertInvalid(
            OrganizationKeyInventoryParser.Parse(Doc(2, "[" + v2WithCertificate + "]")),
            OrganizationKeyInventoryInvalidReason.ProhibitedField);

        string v1WithPrivateKey = "{ \"entryVersion\": 1, \"privateKey\": \"x\" }";
        AssertInvalid(
            OrganizationKeyInventoryParser.Parse(Doc(1, "[" + v1WithPrivateKey + "]")),
            OrganizationKeyInventoryInvalidReason.ProhibitedField);

        AssertInvalid(
            OrganizationKeyInventoryParser.Parse("{ \"schemaVersion\": 2, \"entries\": [], \"storeName\": \"My\" }"),
            OrganizationKeyInventoryInvalidReason.ProhibitedField);
    }

    [Fact] // C11b — a v1 entry may not carry a reference at all.
    public void C11b_SchemaOne_WithCertificateReference_UnknownField()
    {
        AssertInvalid(
            OrganizationKeyInventoryParser.Parse(Doc(1, "[" + V1Entry(certRef: RefLowerA) + "]")),
            OrganizationKeyInventoryInvalidReason.UnknownField);
    }

    // ==== B. Pure resolution state model ======================================

    [Fact] // C12 — administrator disablement outranks the reference.
    public void C12_DisabledEntry_Resolves_EntryDisabled()
    {
        OrganizationKeyInventoryEntry entry = ParseSingle(Doc(2, "[" + V2Entry(adminState: "disabled") + "]"));
        CertificateResolution result = OrganizationCertificateResolver.Resolve(
            Provisioned(), entry, SyntheticCertificateCatalog.WithFingerprints(RefUpperA).Query());

        Assert.Equal(CertificateResolutionState.EntryDisabled, result.State);
        Assert.Equal("entry_disabled", result.WireState);
    }

    [Fact] // C13 — a null, unavailable, or disconnected catalog fails closed.
    public void C13_CatalogUnavailable_Resolves_CatalogUnavailable()
    {
        OrganizationKeyInventoryEntry entry = ParseSingle(Doc(2, "[" + V2Entry() + "]"));

        foreach (CertificateCatalogResult? catalog in new[]
        {
            null,
            CertificateCatalogResult.Unavailable(),
            CertificateCatalogResult.NotConnected(),
            new DisabledProductionCertificateCatalog().Query(),
        })
        {
            CertificateResolution result = OrganizationCertificateResolver.Resolve(Provisioned(), entry, catalog);
            Assert.Equal(CertificateResolutionState.CatalogUnavailable, result.State);
            Assert.Equal("catalog_unavailable", result.WireState);
        }
    }

    [Fact] // C14 — zero matches is NotFound, never a permissive fallback.
    public void C14_ZeroMatches_Resolves_NotFound()
    {
        OrganizationKeyInventoryEntry entry = ParseSingle(Doc(2, "[" + V2Entry() + "]"));

        CertificateResolution empty = OrganizationCertificateResolver.Resolve(
            Provisioned(), entry, SyntheticCertificateCatalog.WithFingerprints().Query());
        Assert.Equal(CertificateResolutionState.NotFound, empty.State);

        CertificateResolution other = OrganizationCertificateResolver.Resolve(
            Provisioned(), entry, SyntheticCertificateCatalog.WithFingerprints(RefUpperB).Query());
        Assert.Equal(CertificateResolutionState.NotFound, other.State);
        Assert.Equal("not_found", other.WireState);
    }

    [Fact] // C15 — exactly one match is a METADATA match only.
    public void C15_SingleMatch_Resolves_ResolvedMetadataOnly()
    {
        OrganizationKeyInventoryEntry entry = ParseSingle(Doc(2, "[" + V2Entry() + "]"));
        CertificateResolution result = OrganizationCertificateResolver.Resolve(
            Provisioned(), entry, SyntheticCertificateCatalog.WithFingerprints(RefUpperB, RefLowerA).Query());

        Assert.Equal(CertificateResolutionState.ResolvedMetadataOnly, result.State);
        Assert.Equal("resolved_metadata_only", result.WireState);
        Assert.True(result.MetadataOnly);
        Assert.True(result.ReadOnly);
    }

    [Fact] // C16 — more than one match is ambiguous, so it fails closed.
    public void C16_MultipleMatches_Resolves_Ambiguous()
    {
        OrganizationKeyInventoryEntry entry = ParseSingle(Doc(2, "[" + V2Entry() + "]"));
        CertificateResolution result = OrganizationCertificateResolver.Resolve(
            Provisioned(), entry, SyntheticCertificateCatalog.WithFingerprints(RefLowerA, RefUpperA, RefUpperB).Query());

        Assert.Equal(CertificateResolutionState.Ambiguous, result.State);
        Assert.Equal("ambiguous", result.WireState);
    }

    [Fact] // C16b — inventory-layer gates precede every entry consideration.
    public void C16b_InventoryLayerGates_PrecedeEntry()
    {
        OrganizationKeyInventoryEntry entry = ParseSingle(Doc(2, "[" + V2Entry() + "]"));
        CertificateCatalogResult catalog = SyntheticCertificateCatalog.WithFingerprints(RefUpperA).Query();

        Assert.Equal(
            CertificateResolutionState.Unknown,
            OrganizationCertificateResolver.Resolve(null, entry, catalog).State);
        Assert.Equal(
            CertificateResolutionState.Unknown,
            OrganizationCertificateResolver.Resolve(Provisioned(), null, catalog).State);
        Assert.Equal(
            CertificateResolutionState.InventoryNotProvisioned,
            OrganizationCertificateResolver.Resolve(
                OrganizationInventoryProjection.AuthorizedNotProvisioned(), entry, catalog).State);
        Assert.Equal(
            CertificateResolutionState.Invalid,
            OrganizationCertificateResolver.Resolve(
                OrganizationInventoryProjection.Unavailable(), entry, catalog).State);
        Assert.Equal(
            CertificateResolutionState.Invalid,
            OrganizationCertificateResolver.Resolve(
                OrganizationInventoryProjection.Untrusted(), entry, catalog).State);
        Assert.Equal(
            CertificateResolutionState.Invalid,
            OrganizationCertificateResolver.Resolve(
                OrganizationInventoryProjection.Invalid(
                    OrganizationKeyInventoryInvalidReason.MalformedJson), entry, catalog).State);
        Assert.Equal(
            CertificateResolutionState.InventoryNotAuthorized,
            OrganizationCertificateResolver.Resolve(
                OrganizationInventoryProjection.FromDeniedGate(
                    ManagedChefKeysGate.Evaluate(MachinePolicyDetection.NotConfigured())), entry, catalog).State);
    }

    [Fact] // C17 — the result exposes no certificate metadata or identifier.
    public void C17_Resolution_ExposesNoCertificateMetadata()
    {
        string[] forbidden = { "fingerprint", "subject", "issuer", "serial", "thumbprint", "sha256", "reference" };
        foreach (MemberInfo member in typeof(CertificateResolution).GetMembers(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            foreach (string token in forbidden)
            {
                Assert.DoesNotContain(token, member.Name, StringComparison.OrdinalIgnoreCase);
            }
        }

        OrganizationKeyInventoryEntry entry = ParseSingle(Doc(2, "[" + V2Entry() + "]"));
        CertificateResolution result = OrganizationCertificateResolver.Resolve(
            Provisioned(), entry, SyntheticCertificateCatalog.WithFingerprints(RefLowerA).Query());

        string text = result.ToString();
        Assert.DoesNotContain(RefLowerA, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("org-key-1", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tenant-ref-1", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("client-ref-1", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("CertificateResolution[state=resolved_metadata_only]", text);
    }

    [Fact] // C18 — resolution never grants a capability, not even when resolved.
    public void C18_ResolvedState_GrantsNoCapability()
    {
        OrganizationKeyInventoryEntry entry = ParseSingle(Doc(2, "[" + V2Entry() + "]"));
        CertificateResolution result = OrganizationCertificateResolver.Resolve(
            Provisioned(), entry, SyntheticCertificateCatalog.WithFingerprints(RefLowerA).Query());

        Assert.Equal(CertificateResolutionState.ResolvedMetadataOnly, result.State);
        Assert.False(result.Usable);
        Assert.False(result.PrivateKeyAvailable);
        Assert.False(result.RecipeBound);
        Assert.False(result.BakeAuthorized);
        Assert.False(result.ServiceReady);

        // The inventory projection is untouched by resolution.
        OrganizationInventoryProjection projection = Provisioned();
        Assert.False(projection.CertificateResolved);
        Assert.False(projection.PrivateKeyAvailable);
        Assert.False(projection.Usable);
        Assert.False(projection.RecipeBound);
        Assert.False(projection.BakeAuthorized);
        Assert.False(projection.ServiceReady);
    }

    [Fact] // C19 — the synthetic catalog is deterministic and side-effect-free.
    public void C19_SyntheticCatalog_IsDeterministic()
    {
        SyntheticCertificateCatalog catalog = SyntheticCertificateCatalog.WithFingerprints(RefLowerA, RefUpperB);
        OrganizationKeyInventoryEntry entry = ParseSingle(Doc(2, "[" + V2Entry() + "]"));

        CertificateCatalogResult first = catalog.Query();
        for (int i = 0; i < 5; i++)
        {
            CertificateCatalogResult again = catalog.Query();
            Assert.Equal(first.Status, again.Status);
            Assert.Equal(first.Occurrences.Count, again.Occurrences.Count);
            for (int j = 0; j < first.Occurrences.Count; j++)
            {
                Assert.Equal(first.Occurrences[j].Fingerprint, again.Occurrences[j].Fingerprint);
                Assert.Equal(first.Occurrences[j].Ordinal, again.Occurrences[j].Ordinal);
            }
            Assert.Equal(
                CertificateResolutionState.ResolvedMetadataOnly,
                OrganizationCertificateResolver.Resolve(Provisioned(), entry, again).State);
        }

        // Two independently built catalogs with the same input agree exactly.
        SyntheticCertificateCatalog twin = SyntheticCertificateCatalog.WithFingerprints(RefLowerA, RefUpperB);
        Assert.Equal(first.Occurrences.Count, twin.Query().Occurrences.Count);
        Assert.Equal(first.Occurrences[0].Fingerprint, twin.Query().Occurrences[0].Fingerprint);
    }

    // ==== C. Deterministic containment source scan (NEW file only) ============

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        string dir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // Strip block comments, line comments, AND string/char literals so descriptive
    // doctrine comments and bounded token literals cannot false-positive a
    // CODE-pattern scan.
    private static string StripCommentsAndStrings(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        src = Regex.Replace(src, @"//[^\r\n]*", " ");
        src = Regex.Replace(src, "@\"(?:[^\"]|\"\")*\"", " ");
        src = Regex.Replace(src, "\"(?:\\\\.|[^\"\\\\])*\"", " ");
        src = Regex.Replace(src, "'(?:\\\\.|[^'\\\\])*'", " ");
        return src;
    }

    private const string ResolutionRel =
        "src/PAXCookbook.Shared/Contracts/OrganizationCertificateResolutionContract.cs";

    [Fact] // C20 — the resolution contract opens no certificate store and reads no key.
    public void C20_ResolutionContract_NoCertificateStoreOrPrivateKey()
    {
        string src = StripCommentsAndStrings(ReadSource(ResolutionRel));
        Assert.DoesNotContain("X509Store", src);
        Assert.DoesNotContain("X509Certificate", src);
        Assert.DoesNotContain("StoreName", src);
        Assert.DoesNotContain("FindByThumbprint", src);
        Assert.DoesNotContain("GetRSAPrivateKey", src);
        Assert.DoesNotContain(".PrivateKey", src);

        // It also reaches no store/vault/graph/token/service/PAX surface at all.
        Assert.DoesNotContain("System.Security.Cryptography", src);
        Assert.DoesNotContain("File.", src);
        Assert.DoesNotContain("Directory.", src);
        Assert.DoesNotContain("Registry", src);
        Assert.DoesNotContain("HttpClient", src);
        Assert.DoesNotContain("Process.Start", src);
        Assert.DoesNotContain("Microsoft.Identity", src);
        Assert.DoesNotContain("GetEnvironmentVariable", src);
    }

    // ==== D. Regression: Cycle-5 doctrine that must not move ==================

    [Fact] // C21 — a provisioned projection still grants nothing.
    public void C21_Regression_ProvisionedProjection_GrantsNothing()
    {
        OrganizationInventoryProjection projection = OrganizationInventoryProjection.AuthorizedProvisioned(2);
        Assert.True(projection.InventoryLoaded);
        Assert.Equal(2, projection.EntryCount);
        Assert.False(projection.CertificateResolved);
        Assert.False(projection.Usable);
        Assert.True(projection.ReadOnly);
        Assert.True(projection.CertificateOnly);
    }

    [Fact] // C22 — entryVersion 2 inside a schema-1 document stays invalid.
    public void C22_Regression_EntryVersionTwoInSchemaOne_Invalid()
    {
        string entry = "{ \"entryVersion\": 2, \"organizationKeyId\": \"org-key-1\", \"displayName\": \"n\", "
            + "\"certificateReferenceType\": \"app_registration_certificate\", \"adminState\": \"enabled\", "
            + "\"tenantReference\": \"t\", \"clientReference\": \"c\" }";
        AssertInvalid(
            OrganizationKeyInventoryParser.Parse(Doc(1, "[" + entry + "]")),
            OrganizationKeyInventoryInvalidReason.UnsupportedSchema);
    }

    [Fact] // C23 — the only production catalog is permanently disconnected.
    public void C23_Regression_ProductionCatalog_IsDisconnected()
    {
        var catalog = new DisabledProductionCertificateCatalog();
        for (int i = 0; i < 3; i++)
        {
            CertificateCatalogResult r = catalog.Query();
            Assert.Equal(CertificateCatalogStatus.NotConnected, r.Status);
            Assert.Empty(r.Occurrences);
        }
    }

    // Splits a hex string into 2-character groups so a ':'-separated form can be
    // built for the separator-rejection test.
    private static IEnumerable<string> Chunked2(string value)
    {
        for (int i = 0; i + 2 <= value.Length; i += 2)
        {
            yield return value.Substring(i, 2);
        }
    }
}
