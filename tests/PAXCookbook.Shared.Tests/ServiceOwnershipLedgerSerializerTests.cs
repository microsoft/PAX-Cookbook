using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

// ===========================================================================
// CYCLE 82 - SCHEMA-V3 LEDGER SERIALIZER (D3)
// ===========================================================================
//
// SCOPE, stated plainly. The serializer is PURE: it opens no file, composes no
// path, reads no environment variable, starts no process and performs no I/O.
// Nothing in this file touches a certificate store, an ACL, the registry, the
// SCM or ProgramData. Every fixture value is SYNTHETIC.
//
// THE CORRECTNESS GATE IS THE VALIDATOR. A writer whose only reader is a
// fail-closed parser has exactly one honest proof: the parser must accept what
// the writer emits, must classify it identically, and re-emitting it must
// produce the same bytes.
public class ServiceOwnershipLedgerSerializerTests
{
    private const string OwnerSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string SvcSid = "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890";
    private const string Thumb = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    private const string Thumb2 = "0123456789ABCDEF0123456789ABCDEF01234567";
    private const string ProviderToken = "microsoft-software-key-storage-provider";
    private const string ProfileToken =
        "microsoft-software-ksp-backing-file-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.0-filesystemrights";
    private const string MechanismToken = "microsoft-software-ksp-backing-file-dacl";
    private const string DescriptorFormatToken = "microsoft-software-ksp-backing-file-self-relative-v1";
    private const string KeyStorageRootToken = "microsoft-software-key-storage-provider-machine-keys";
    private const string Stamp = "2026-08-06T00:00:00Z";
    private const string InstallId = "install-0001";
    private const string ApprovedMaskText = "00120009";

    private static readonly byte[] PriorBytes =
        { 0x01, 0x00, 0x04, 0x90, 0x14, 0x00, 0x00, 0x00, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00, 0x20, 0x02, 0x00, 0x00, 0x01, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x15, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0xE9, 0x03, 0x00, 0x00, 0x02, 0x00, 0x34, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x03, 0x14, 0x00, 0xFF, 0x01, 0x1F, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x12, 0x00, 0x00, 0x00, 0x00, 0x03, 0x18, 0x00, 0xFF, 0x01, 0x1F, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00, 0x20, 0x02, 0x00, 0x00 };

    // =======================================================================
    // NOTHING UNACCEPTED IS EVER WRITTEN
    // =======================================================================

    [Fact]
    public void A_null_source_is_refused()
    {
        ServiceOwnershipLedgerSerializationResult result =
            ServiceOwnershipLedgerSerializer.Serialize(null);

        Assert.False(result.IsSerialized);
        Assert.Equal(ServiceOwnershipLedgerSerializationOutcome.SourceNotAccepted, result.Outcome);
        Assert.Equal(string.Empty, result.Json);
        Assert.Empty(result.Utf8Bytes);
    }

    [Fact]
    public void An_absent_ledger_is_refused_because_there_is_no_document_to_write()
    {
        ServiceOwnershipLedgerSerializationResult result =
            ServiceOwnershipLedgerSerializer.Serialize(ServiceOwnershipLedgerValidator.ForAbsentLedger());

        Assert.False(result.IsSerialized);
        Assert.Equal(ServiceOwnershipLedgerSerializationOutcome.SourceNotAccepted, result.Outcome);
        Assert.Empty(result.Utf8Bytes);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":3,\"productOwnershipMarker\":\"someone-else\"}")]
    public void A_refused_document_can_never_be_serialized_as_accepted_state(string json)
    {
        ServiceOwnershipLedgerValidationResult source = ServiceOwnershipLedgerValidator.Validate(json);
        Assert.True(source.IsRefused);

        ServiceOwnershipLedgerSerializationResult result =
            ServiceOwnershipLedgerSerializer.Serialize(source);

        Assert.False(result.IsSerialized);
        Assert.Equal(ServiceOwnershipLedgerSerializationOutcome.SourceNotAccepted, result.Outcome);
        Assert.Equal(string.Empty, result.Json);
    }

    [Fact]
    public void An_unsupported_active_model_can_never_be_serialized()
    {
        // A one-field near miss: the retired CNG mechanism. It is refused on the way
        // in, so it can never reach the writer as accepted state.
        DocModel doc = ActiveDoc();
        doc.Entries[0].GrantMechanism = "cng-security-descriptor";

        ServiceOwnershipLedgerValidationResult source = Validate(doc);
        Assert.True(source.IsRefused);
        Assert.False(ServiceOwnershipLedgerSerializer.Serialize(source).IsSerialized);
    }

    // =======================================================================
    // DETERMINISM AND SHAPE
    // =======================================================================

    [Fact]
    public void The_emitted_document_declares_schema_version_three()
    {
        using JsonDocument parsed = JsonDocument.Parse(SerializeOk(Validate(ActiveDoc())));

        Assert.Equal(
            ServiceOwnershipLedgerContract.LedgerSchemaVersion,
            parsed.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void The_property_order_mirrors_the_parsers_own_closed_lists_exactly()
    {
        using JsonDocument parsed = JsonDocument.Parse(SerializeOk(Validate(ActiveDoc())));

        Assert.Equal(
            ServiceOwnershipLedgerContract.DocumentPropertyNames.ToArray(),
            parsed.RootElement.EnumerateObject().Select(p => p.Name).ToArray());

        foreach (JsonElement entry in parsed.RootElement.GetProperty("entries").EnumerateArray())
        {
            Assert.Equal(
                ServiceOwnershipLedgerContract.EntryPropertyNames.ToArray(),
                entry.EnumerateObject().Select(p => p.Name).ToArray());
        }
    }

    [Fact]
    public void The_emitted_bytes_are_utf8_without_a_byte_order_mark()
    {
        ServiceOwnershipLedgerSerializationResult result =
            ServiceOwnershipLedgerSerializer.Serialize(Validate(ActiveDoc()));

        Assert.True(result.IsSerialized);
        Assert.True(result.Utf8Bytes.Length >= 3);
        Assert.False(
            result.Utf8Bytes[0] == 0xEF && result.Utf8Bytes[1] == 0xBB && result.Utf8Bytes[2] == 0xBF,
            "the emitted bytes must never carry a byte-order mark");

        Assert.Equal(
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(result.Json),
            result.Utf8Bytes);
    }

    [Fact]
    public void The_emitted_document_carries_no_comment_and_no_unknown_field()
    {
        string json = SerializeOk(Validate(ActiveDoc()));

        Assert.DoesNotContain("//", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/*", json, StringComparison.Ordinal);

        using JsonDocument parsed = JsonDocument.Parse(json);
        var documentNames = new HashSet<string>(
            ServiceOwnershipLedgerContract.DocumentPropertyNames, StringComparer.Ordinal);
        foreach (JsonProperty property in parsed.RootElement.EnumerateObject())
        {
            Assert.Contains(property.Name, documentNames);
        }

        var entryNames = new HashSet<string>(
            ServiceOwnershipLedgerContract.EntryPropertyNames, StringComparer.Ordinal);
        foreach (JsonElement entry in parsed.RootElement.GetProperty("entries").EnumerateArray())
        {
            foreach (JsonProperty property in entry.EnumerateObject())
            {
                Assert.Contains(property.Name, entryNames);
            }
        }

        // Nothing from the prohibited vocabulary can appear, in any casing.
        foreach (string prohibited in ServiceOwnershipLedgerContract.ProhibitedPropertyNames)
        {
            Assert.DoesNotContain("\"" + prohibited + "\":", json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_restore_payload_and_binding_survive_serialization()
    {
        using JsonDocument parsed = JsonDocument.Parse(SerializeOk(Validate(ActiveDoc())));
        JsonElement entry = parsed.RootElement.GetProperty("entries")[0];

        Assert.Equal(
            Convert.ToBase64String(PriorBytes),
            entry.GetProperty("priorDaclBytesBase64").GetString());
        Assert.Equal(
            UpperHex(SHA256.HashData(PriorBytes)),
            entry.GetProperty("priorDaclSha256").GetString());
        Assert.Equal(64, entry.GetProperty("capturedStateBindingSha256").GetString()!.Length);
        Assert.Equal(
            new[] { "job-0001" },
            entry.GetProperty("associatedPromotedJobIds").EnumerateArray()
                .Select(v => v.GetString()).ToArray());
    }

    [Fact]
    public void Entry_and_promoted_job_ordering_is_stable_and_ordinal()
    {
        DocModel doc = ActiveDoc();
        doc.Entries[0].EntryId = "entry-0002";
        doc.Entries[0].JobIds = new[] { "job-0009", "job-0001", "job-0005" };
        doc.Entries.Add(SecondActiveEntry());

        string json = SerializeOk(Validate(doc));
        using JsonDocument parsed = JsonDocument.Parse(json);

        Assert.Equal(
            new[] { "entry-0001", "entry-0002" },
            parsed.RootElement.GetProperty("entries").EnumerateArray()
                .Select(e => e.GetProperty("entryId").GetString()).ToArray());

        JsonElement second = parsed.RootElement.GetProperty("entries")[1];
        Assert.Equal(
            new[] { "job-0001", "job-0005", "job-0009" },
            second.GetProperty("associatedPromotedJobIds").EnumerateArray()
                .Select(v => v.GetString()).ToArray());
    }

    [Fact]
    public void Serialize_validate_serialize_is_byte_identical()
    {
        foreach (DocModel doc in new[] { ActiveDoc(), IntendedDoc(), EmptyDoc() })
        {
            string first = SerializeOk(Validate(doc));
            string second = SerializeOk(ServiceOwnershipLedgerValidator.Validate(first));
            string third = SerializeOk(ServiceOwnershipLedgerValidator.Validate(second));

            Assert.Equal(first, second, StringComparer.Ordinal);
            Assert.Equal(second, third, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void Parse_of_serialize_returns_an_accepted_equivalent_document()
    {
        foreach (DocModel doc in new[] { ActiveDoc(), IntendedDoc(), EmptyDoc() })
        {
            ServiceOwnershipLedgerValidationResult source = Validate(doc);
            Assert.True(source.IsAccepted);

            ServiceOwnershipLedgerValidationResult reparsed =
                ServiceOwnershipLedgerValidator.Validate(SerializeOk(source));

            Assert.True(reparsed.IsAccepted);
            Assert.Equal(source.Outcome, reparsed.Outcome);
            AssertEquivalent(source.Document!, reparsed.Document!);
        }
    }

    [Fact]
    public void An_active_round_trip_returns_the_new_active_outcome()
    {
        ServiceOwnershipLedgerValidationResult source = Validate(ActiveDoc());
        ServiceOwnershipLedgerValidationResult reparsed =
            ServiceOwnershipLedgerValidator.Validate(SerializeOk(source));

        Assert.Equal("Active", source.Outcome.ToString());
        Assert.Equal(source.Outcome, reparsed.Outcome);
    }

    // =======================================================================
    // HELPERS
    // =======================================================================

    private static void AssertEquivalent(
        ServiceOwnershipLedgerDocument left, ServiceOwnershipLedgerDocument right)
    {
        Assert.Equal(left.SchemaVersion, right.SchemaVersion);
        Assert.Equal(left.ProductOwnershipMarker, right.ProductOwnershipMarker);
        Assert.Equal(left.ManagedFeatureId, right.ManagedFeatureId);
        Assert.Equal(left.InstallationOwnershipId, right.InstallationOwnershipId);
        Assert.Equal(left.Generation, right.Generation);
        Assert.Equal(left.TransactionState, right.TransactionState);
        Assert.Equal(left.CreatedUtc, right.CreatedUtc);
        Assert.Equal(left.UpdatedUtc, right.UpdatedUtc);
        Assert.Equal(left.LastOperationId, right.LastOperationId);
        Assert.Equal(left.Entries.Count, right.Entries.Count);

        List<ServiceOwnershipLedgerEntry> leftEntries =
            left.Entries.OrderBy(e => e.EntryId, StringComparer.Ordinal).ToList();
        List<ServiceOwnershipLedgerEntry> rightEntries =
            right.Entries.OrderBy(e => e.EntryId, StringComparer.Ordinal).ToList();

        for (int i = 0; i < leftEntries.Count; i++)
        {
            ServiceOwnershipLedgerEntry a = leftEntries[i];
            ServiceOwnershipLedgerEntry b = rightEntries[i];

            Assert.Equal(a.EntryId, b.EntryId);
            Assert.Equal(a.OwningUserSid, b.OwningUserSid);
            Assert.Equal(a.ServiceSid, b.ServiceSid);
            Assert.Equal(a.CredentialKind, b.CredentialKind);
            Assert.Equal(a.CertificateThumbprintSha1, b.CertificateThumbprintSha1);
            Assert.Equal(a.Provenance, b.Provenance);
            Assert.Equal(a.PrivateKeyProviderKind, b.PrivateKeyProviderKind);
            Assert.Equal(a.RightsProfileId, b.RightsProfileId);
            Assert.Equal(a.KeyIdentity, b.KeyIdentity);
            Assert.Equal(a.GrantMechanism, b.GrantMechanism);
            Assert.Equal(a.GrantedRightsMask, b.GrantedRightsMask);
            Assert.Equal(a.RightsPolicyVersion, b.RightsPolicyVersion);
            Assert.Equal(a.PriorDaclState, b.PriorDaclState);
            Assert.Equal(a.PriorDaclBytesBase64, b.PriorDaclBytesBase64);
            Assert.Equal(a.PriorDaclSha256, b.PriorDaclSha256);
            Assert.Equal(a.CapturedStateBindingSha256, b.CapturedStateBindingSha256);
            Assert.Equal(
                a.AssociatedPromotedJobIds.OrderBy(j => j, StringComparer.Ordinal).ToArray(),
                b.AssociatedPromotedJobIds.OrderBy(j => j, StringComparer.Ordinal).ToArray());
            Assert.Equal(a.LifecycleState, b.LifecycleState);
            Assert.Equal(a.CreatedUtc, b.CreatedUtc);
            Assert.Equal(a.UpdatedUtc, b.UpdatedUtc);
            Assert.Equal(a.ProviderUniqueName, b.ProviderUniqueName);
            Assert.Equal(a.KeyStorageRoot, b.KeyStorageRoot);
            Assert.Equal(a.DescriptorFormat, b.DescriptorFormat);
        }
    }

    private static string SerializeOk(ServiceOwnershipLedgerValidationResult source)
    {
        ServiceOwnershipLedgerSerializationResult result =
            ServiceOwnershipLedgerSerializer.Serialize(source);
        Assert.True(
            result.IsSerialized,
            "the serializer refused an accepted document with outcome " + result.Outcome);
        return result.Json;
    }

    private static ServiceOwnershipLedgerValidationResult Validate(DocModel doc) =>
        ServiceOwnershipLedgerValidator.Validate(Build(doc));

    private static DocModel ActiveDoc()
    {
        DocModel doc = new() { TransactionState = "done" };
        doc.Entries.Add(new EntryModel());
        return doc;
    }

    private static DocModel IntendedDoc()
    {
        DocModel doc = new() { TransactionState = "preparing" };
        doc.Entries.Add(new EntryModel
        {
            GrantedRightsMask = "00000081",
            LifecycleState = "intended",
        });
        return doc;
    }

    private static DocModel EmptyDoc() => new() { TransactionState = "idle" };

    private static EntryModel SecondActiveEntry() => new()
    {
        EntryId = "entry-0001",
        CertificateThumbprintSha1 = Thumb2,
        KeyIdentity = "synthetic-key-identity_02.test",
        ProviderUniqueName = "synthetic-unique-leaf_02.pvk",
        JobIds = new[] { "job-0002" },
    };

    private static string UpperHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private sealed class DocModel
    {
        public string TransactionState = "done";
        public List<EntryModel> Entries = new();
    }

    private sealed class EntryModel
    {
        public string EntryId = "entry-0002";
        public string CertificateThumbprintSha1 = Thumb;
        public string KeyIdentity = "synthetic-key-identity_01.test";
        public string ProviderUniqueName = "synthetic-unique-leaf_01.pvk";
        public string GrantedRightsMask = ApprovedMaskText;
        public string GrantMechanism = MechanismToken;
        public string LifecycleState = "active";
        public string[] JobIds = { "job-0001" };
    }

    private static string Q(string value) => "\"" + value + "\"";

    private static string Build(DocModel doc)
    {
        var sb = new StringBuilder("{");
        sb.Append(Q("schemaVersion")).Append(":3");
        sb.Append(',').Append(Q("productOwnershipMarker")).Append(':')
          .Append(Q(ServiceOwnershipLedgerContract.ProductOwnershipMarker));
        sb.Append(',').Append(Q("managedFeatureId")).Append(':')
          .Append(Q(ServiceOwnershipLedgerContract.ManagedFeatureId));
        sb.Append(',').Append(Q("installationOwnershipId")).Append(':').Append(Q(InstallId));
        sb.Append(',').Append(Q("generation")).Append(":1");
        sb.Append(',').Append(Q("transactionState")).Append(':').Append(Q(doc.TransactionState));
        sb.Append(',').Append(Q("entries")).Append(':').Append('[');
        for (int i = 0; i < doc.Entries.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(BuildEntry(doc.Entries[i]));
        }
        sb.Append(']');
        sb.Append(',').Append(Q("createdUtc")).Append(':').Append(Q(Stamp));
        sb.Append(',').Append(Q("updatedUtc")).Append(':').Append(Q(Stamp));
        sb.Append(',').Append(Q("lastOperationId")).Append(':').Append(Q("op-0001"));
        return sb.Append('}').ToString();
    }

    private static string BuildEntry(EntryModel e)
    {
        ServiceOwnershipLedgerContract.TryParseWireToken(
            ProviderToken, out ServiceOwnershipPrivateKeyProviderKind provider);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            ProfileToken, out ServiceOwnershipRightsProfileId profile);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.GrantMechanism, out ServiceOwnershipGrantMechanism mechanism);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            "present", out ServiceOwnershipPriorDaclState priorState);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            KeyStorageRootToken, out ServiceOwnershipKeyStorageRoot keyStorageRoot);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            DescriptorFormatToken, out ServiceOwnershipDescriptorFormat descriptorFormat);

        string priorSha = UpperHex(SHA256.HashData(PriorBytes));
        string binding = ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            1, OwnerSid, SvcSid, e.CertificateThumbprintSha1, provider, profile, e.KeyIdentity,
            mechanism, e.GrantedRightsMask, priorState, priorSha, e.ProviderUniqueName,
            keyStorageRoot, descriptorFormat);

        var sb = new StringBuilder("{");
        sb.Append(Q("entryId")).Append(':').Append(Q(e.EntryId));
        sb.Append(',').Append(Q("owningUserSid")).Append(':').Append(Q(OwnerSid));
        sb.Append(',').Append(Q("serviceSid")).Append(':').Append(Q(SvcSid));
        sb.Append(',').Append(Q("credentialKind")).Append(':')
          .Append(Q("personal-app-registration-certificate"));
        sb.Append(',').Append(Q("certificateThumbprintSha1")).Append(':')
          .Append(Q(e.CertificateThumbprintSha1));
        sb.Append(',').Append(Q("provenance")).Append(':').Append(Q("referenced"));
        sb.Append(',').Append(Q("privateKeyProviderKind")).Append(':').Append(Q(ProviderToken));
        sb.Append(',').Append(Q("rightsProfileId")).Append(':').Append(Q(ProfileToken));
        sb.Append(',').Append(Q("keyIdentity")).Append(':').Append(Q(e.KeyIdentity));
        sb.Append(',').Append(Q("grantMechanism")).Append(':').Append(Q(e.GrantMechanism));
        sb.Append(',').Append(Q("grantedRightsMask")).Append(':').Append(Q(e.GrantedRightsMask));
        sb.Append(',').Append(Q("rightsPolicyVersion")).Append(":3");
        sb.Append(',').Append(Q("priorDaclState")).Append(':').Append(Q("present"));
        sb.Append(',').Append(Q("priorDaclBytesBase64")).Append(':')
          .Append(Q(Convert.ToBase64String(PriorBytes)));
        sb.Append(',').Append(Q("priorDaclSha256")).Append(':').Append(Q(priorSha));
        sb.Append(',').Append(Q("capturedStateBindingSha256")).Append(':').Append(Q(binding));
        sb.Append(',').Append(Q("associatedPromotedJobIds")).Append(':')
          .Append('[').Append(string.Join(",", e.JobIds.Select(Q))).Append(']');
        sb.Append(',').Append(Q("lifecycleState")).Append(':').Append(Q(e.LifecycleState));
        sb.Append(',').Append(Q("createdUtc")).Append(':').Append(Q(Stamp));
        sb.Append(',').Append(Q("updatedUtc")).Append(':').Append(Q(Stamp));
        sb.Append(',').Append(Q("providerUniqueName")).Append(':').Append(Q(e.ProviderUniqueName));
        sb.Append(',').Append(Q("keyStorageRoot")).Append(':').Append(Q(KeyStorageRootToken));
        sb.Append(',').Append(Q("descriptorFormat")).Append(':').Append(Q(DescriptorFormatToken));
        return sb.Append('}').ToString();
    }
}
