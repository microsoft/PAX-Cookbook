using System;
using System.IO;
using System.Text;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 58 - INSTALLATION ANCHOR CONTRACT + STORE (Setup only)
// ===========================================================================
//
// SCOPE. These tests exercise the anchor schema/validator and the four write
// behaviours the cycle requires: a new write, a matching idempotent write, a
// conflicting write refusal, and a malformed/partial state refusal. They run
// entirely against an OS TEMP directory through the disclosed internal seam;
// nothing here touches %ProgramData%, elevates, prompts for UAC, installs or
// contacts a service, mutates a certificate or key, spawns a process, runs PAX
// or starts a Bake.
public sealed class ServiceInstallationAnchorContractTests
{
    private const string ClassicUserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    // Entra-joined shape. Every subauthority must fit in a uint32: a larger
    // value would make this fixture unparseable, and the assertions below would
    // then pass vacuously instead of proving anything.
    private const string EntraUserSid = "S-1-12-1-1111111111-2222222222-3333333333-1234567890";
    private const string InstallationId = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";

    private static string ValidAnchorJson(
        string installationId = InstallationId,
        string sid = ClassicUserSid,
        string createdUtc = "2026-08-16T12:00:00Z")
    {
        Assert.True(ServiceInstallationAnchorContract.TrySerialize(
            new ServiceInstallationAnchorDocument(installationId, sid, createdUtc), out string json));
        return json;
    }

    // ---- acceptance --------------------------------------------------------

    [Fact]
    public void A_well_formed_anchor_is_accepted_and_round_trips_exactly()
    {
        string json = ValidAnchorJson();

        ServiceInstallationAnchorValidationResult result = ServiceInstallationAnchorValidator.Validate(json);

        Assert.True(result.IsAccepted);
        Assert.Equal(ServiceInstallationAnchorOutcome.Valid, result.Outcome);
        Assert.Equal(ServiceInstallationAnchorInvalidReason.None, result.Reason);
        Assert.NotNull(result.Document);
        Assert.Equal(InstallationId, result.Document!.InstallationId);
        Assert.Equal(ClassicUserSid, result.Document.InitiatingUserSid);

        Assert.True(ServiceInstallationAnchorContract.TrySerialize(result.Document, out string reserialized));
        Assert.Equal(json, reserialized);
    }

    [Fact]
    public void An_entra_style_initiating_user_sid_is_accepted()
    {
        // The ownership ledger's S-1-5-21-only user predicate would refuse this.
        // Refusing an Entra-joined machine's initiating user would be a false
        // negative on exactly the machines this product targets. The SID must
        // genuinely PARSE, or the refusal below would prove nothing.
        Assert.True(ServiceInstallationAnchorContract.IsCanonicalSidString(EntraUserSid));
        Assert.False(ServiceOwnershipLedgerContract.IsUserOwnerSid(EntraUserSid));
        Assert.True(ServiceInstallationAnchorContract.IsInitiatingUserSid(EntraUserSid));

        ServiceInstallationAnchorValidationResult result =
            ServiceInstallationAnchorValidator.Validate(ValidAnchorJson(sid: EntraUserSid));

        Assert.True(result.IsAccepted);
        Assert.Equal(EntraUserSid, result.Document!.InitiatingUserSid);
    }

    [Fact]
    public void Absence_is_only_ever_stated_explicitly()
    {
        ServiceInstallationAnchorValidationResult absent = ServiceInstallationAnchorValidator.ForAbsentAnchor();

        Assert.True(absent.IsAccepted);
        Assert.Equal(ServiceInstallationAnchorOutcome.Absent, absent.Outcome);
        Assert.Null(absent.Document);

        // Blank text is a REFUSAL, never absence: a truncated write must never
        // read as a clean machine.
        foreach (string? blank in new[] { null, "", "   ", "\n" })
        {
            ServiceInstallationAnchorValidationResult result = ServiceInstallationAnchorValidator.Validate(blank);
            Assert.True(result.IsRefused);
            Assert.Equal(ServiceInstallationAnchorInvalidReason.MalformedJson, result.Reason);
        }
    }

    [Fact]
    public void A_refusal_factory_can_never_fabricate_an_acceptance()
    {
        ServiceInstallationAnchorValidationResult remapped =
            ServiceInstallationAnchorValidator.ForRefusedAnchor(ServiceInstallationAnchorInvalidReason.None);

        Assert.True(remapped.IsRefused);
        Assert.Equal(ServiceInstallationAnchorInvalidReason.MalformedJson, remapped.Reason);
    }

    // ---- schema refusals ---------------------------------------------------

    public static TheoryData<string, ServiceInstallationAnchorInvalidReason> RefusedDocuments() => new()
    {
        { "{ not json", ServiceInstallationAnchorInvalidReason.MalformedJson },
        { "[]", ServiceInstallationAnchorInvalidReason.MalformedJson },
        {
            ValidAnchorJson().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal),
            ServiceInstallationAnchorInvalidReason.UnsupportedSchemaVersion
        },
        {
            ValidAnchorJson().Replace("\"schemaVersion\": 1", "\"schemaVersion\": \"1\"", StringComparison.Ordinal),
            ServiceInstallationAnchorInvalidReason.WrongType
        },
        {
            ValidAnchorJson().Replace(
                ServiceInstallationAnchorContract.ProductAnchorMarker, "SomeOtherProduct.v1", StringComparison.Ordinal),
            ServiceInstallationAnchorInvalidReason.WrongMarker
        },
        {
            ValidAnchorJson().Replace(
                ServiceInstallationAnchorContract.ManagedFeatureId, "service-credential-ownership", StringComparison.Ordinal),
            ServiceInstallationAnchorInvalidReason.WrongFeatureId
        },
        {
            ValidAnchorJson().Replace(InstallationId, "3F2504E0-4F89-41D3-9A0C-0305E82C3301", StringComparison.Ordinal),
            ServiceInstallationAnchorInvalidReason.InvalidInstallationId
        },
        {
            ValidAnchorJson().Replace(InstallationId, "00000000-0000-0000-0000-000000000000", StringComparison.Ordinal),
            ServiceInstallationAnchorInvalidReason.InvalidInstallationId
        },
        {
            ValidAnchorJson().Replace(ClassicUserSid, "S-1-5-18", StringComparison.Ordinal),
            ServiceInstallationAnchorInvalidReason.InvalidInitiatingUserSid
        },
        {
            ValidAnchorJson().Replace(ClassicUserSid, "CONTOSO\\\\someone", StringComparison.Ordinal),
            ServiceInstallationAnchorInvalidReason.InvalidInitiatingUserSid
        },
        {
            ValidAnchorJson().Replace("2026-08-16T12:00:00Z", "2026-08-16T12:00:00.000Z", StringComparison.Ordinal),
            ServiceInstallationAnchorInvalidReason.InvalidTimestamp
        },
        {
            ValidAnchorJson().Replace("\"createdUtc\"", "\"upn\"", StringComparison.Ordinal),
            ServiceInstallationAnchorInvalidReason.ProhibitedProperty
        },
        {
            ValidAnchorJson().Replace("\"createdUtc\"", "\"createdUtcExtra\"", StringComparison.Ordinal),
            ServiceInstallationAnchorInvalidReason.UnknownProperty
        },
        {
            ValidAnchorJson().Replace(
                "  \"createdUtc\": \"2026-08-16T12:00:00Z\"\n",
                "  \"createdUtc\": \"2026-08-16T12:00:00Z\",\n  \"createdUtc\": \"2026-08-16T12:00:00Z\"\n",
                StringComparison.Ordinal),
            ServiceInstallationAnchorInvalidReason.DuplicateProperty
        },
    };

    [Theory]
    [MemberData(nameof(RefusedDocuments))]
    public void Every_malformed_unsupported_or_foreign_document_is_refused_with_a_bounded_reason(
        string json, ServiceInstallationAnchorInvalidReason expected)
    {
        ServiceInstallationAnchorValidationResult result = ServiceInstallationAnchorValidator.Validate(json);

        Assert.True(result.IsRefused);
        Assert.Equal(expected, result.Reason);
        Assert.Null(result.Document);
    }

    [Fact]
    public void A_missing_property_is_refused()
    {
        string json = ValidAnchorJson().Replace(
            "  \"managedFeatureId\": \"" + ServiceInstallationAnchorContract.ManagedFeatureId + "\",\n",
            string.Empty,
            StringComparison.Ordinal);

        ServiceInstallationAnchorValidationResult result = ServiceInstallationAnchorValidator.Validate(json);

        Assert.Equal(ServiceInstallationAnchorInvalidReason.MissingProperty, result.Reason);
    }

    [Fact]
    public void An_oversized_document_is_refused_as_oversized_not_malformed()
    {
        string padded = "{\"schemaVersion\":1,\"pad\":\""
            + new string('a', ServiceInstallationAnchorContract.MaxAnchorBytes)
            + "\"}";

        ServiceInstallationAnchorValidationResult result = ServiceInstallationAnchorValidator.Validate(padded);

        Assert.Equal(ServiceInstallationAnchorInvalidReason.OversizedInput, result.Reason);
    }

    // ---- the two schemas are disjoint, in both directions -------------------

    [Fact]
    public void The_anchor_validator_refuses_an_ownership_ledger_document_and_vice_versa()
    {
        string ledgerJson = "{\"schemaVersion\":3,\"productOwnershipMarker\":\""
            + ServiceOwnershipLedgerContract.ProductOwnershipMarker
            + "\",\"managedFeatureId\":\"" + ServiceOwnershipLedgerContract.ManagedFeatureId + "\"}";

        Assert.True(ServiceInstallationAnchorValidator.Validate(ledgerJson).IsRefused);
        Assert.True(ServiceOwnershipLedgerValidator.Validate(ValidAnchorJson()).IsRefused);

        // Two different leaf names, one name authority, no derivation between them.
        Assert.NotEqual(
            ServiceMachineStorageContract.OwnershipLedgerFileName,
            ServiceMachineStorageContract.InstallationAnchorFileName);
        Assert.Equal("installation-anchor.json", ServiceMachineStorageContract.InstallationAnchorFileName);
    }

    // ---- serialization refuses to emit an invalid anchor --------------------

    [Fact]
    public void Serialization_refuses_every_field_the_validator_would_refuse()
    {
        Assert.False(ServiceInstallationAnchorContract.TrySerialize(null, out _));
        Assert.False(ServiceInstallationAnchorContract.TrySerialize(
            new ServiceInstallationAnchorDocument("not-a-guid", ClassicUserSid, "2026-08-16T12:00:00Z"), out _));
        Assert.False(ServiceInstallationAnchorContract.TrySerialize(
            new ServiceInstallationAnchorDocument(InstallationId, "S-1-5-18", "2026-08-16T12:00:00Z"), out _));
        Assert.False(ServiceInstallationAnchorContract.TrySerialize(
            new ServiceInstallationAnchorDocument(InstallationId, ClassicUserSid, "yesterday"), out _));
    }

    [Fact]
    public void Immutable_identity_ignores_the_timestamp_but_never_the_id_or_the_sid()
    {
        var first = new ServiceInstallationAnchorDocument(InstallationId, ClassicUserSid, "2026-08-16T12:00:00Z");
        var laterTimestamp = new ServiceInstallationAnchorDocument(InstallationId, ClassicUserSid, "2027-01-01T00:00:00Z");
        var otherId = new ServiceInstallationAnchorDocument(
            "9f2504e0-4f89-41d3-9a0c-0305e82c3301", ClassicUserSid, "2026-08-16T12:00:00Z");
        var otherSid = new ServiceInstallationAnchorDocument(InstallationId, EntraUserSid, "2026-08-16T12:00:00Z");

        Assert.True(ServiceInstallationAnchorContract.HasIdenticalImmutableIdentity(first, laterTimestamp));
        Assert.False(ServiceInstallationAnchorContract.HasIdenticalImmutableIdentity(first, otherId));
        Assert.False(ServiceInstallationAnchorContract.HasIdenticalImmutableIdentity(first, otherSid));
        Assert.False(ServiceInstallationAnchorContract.HasIdenticalImmutableIdentity(first, null));
    }
}

public sealed class ServiceInstallationAnchorStoreTests : IDisposable
{
    private const string ClassicUserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string OtherUserSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string InstallationId = "3f2504e0-4f89-41d3-9a0c-0305e82c3301";
    private const string OtherInstallationId = "9f2504e0-4f89-41d3-9a0c-0305e82c3301";

    private readonly string _serviceDirectory;

    public ServiceInstallationAnchorStoreTests()
    {
        _serviceDirectory = Path.Combine(
            Path.GetTempPath(), "paxcookbook-cycle58-anchor", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_serviceDirectory))
            {
                Directory.Delete(_serviceDirectory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best effort temp cleanup.
        }
    }

    private string AnchorPath =>
        Path.Combine(_serviceDirectory, ServiceMachineStorageContract.InstallationAnchorFileName);

    private static ServiceInstallationAnchorDocument Doc(
        string installationId = InstallationId,
        string sid = ClassicUserSid,
        string createdUtc = "2026-08-16T12:00:00Z") => new(installationId, sid, createdUtc);

    private void SeedRawAnchor(string content)
    {
        Directory.CreateDirectory(_serviceDirectory);
        File.WriteAllText(AnchorPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // ---- new write ---------------------------------------------------------

    [Fact]
    public void A_first_write_creates_the_anchor_and_leaves_no_temporary_behind()
    {
        Assert.Equal(
            ServiceInstallationAnchorReadState.Absent,
            ServiceInstallationAnchorStore.ReadFrom(_serviceDirectory).State);

        ServiceInstallationAnchorWriteState written =
            ServiceInstallationAnchorStore.WriteTo(_serviceDirectory, Doc());

        Assert.Equal(ServiceInstallationAnchorWriteState.Created, written);
        Assert.True(File.Exists(AnchorPath));
        Assert.Empty(Directory.GetFiles(_serviceDirectory, "*.tmp"));

        ServiceInstallationAnchorReadResult read = ServiceInstallationAnchorStore.ReadFrom(_serviceDirectory);
        Assert.Equal(ServiceInstallationAnchorReadState.Validated, read.State);
        Assert.Equal(InstallationId, read.Validation!.Document!.InstallationId);
        Assert.Equal(ClassicUserSid, read.Validation.Document.InitiatingUserSid);
    }

    // ---- matching idempotent write -----------------------------------------

    [Fact]
    public void A_matching_write_is_idempotent_and_does_not_rewrite_the_file()
    {
        Assert.Equal(
            ServiceInstallationAnchorWriteState.Created,
            ServiceInstallationAnchorStore.WriteTo(_serviceDirectory, Doc()));
        byte[] before = File.ReadAllBytes(AnchorPath);

        // A LATER timestamp with the SAME immutable identity is still idempotent.
        ServiceInstallationAnchorWriteState again = ServiceInstallationAnchorStore.WriteTo(
            _serviceDirectory, Doc(createdUtc: "2027-01-01T00:00:00Z"));

        Assert.Equal(ServiceInstallationAnchorWriteState.AlreadyMatching, again);
        Assert.Equal(before, File.ReadAllBytes(AnchorPath));
    }

    // ---- conflicting write refusal -----------------------------------------

    [Theory]
    [InlineData(OtherInstallationId, ClassicUserSid)]
    [InlineData(InstallationId, OtherUserSid)]
    [InlineData(OtherInstallationId, OtherUserSid)]
    public void A_write_whose_identity_differs_is_refused_and_never_overwrites(
        string installationId, string sid)
    {
        Assert.Equal(
            ServiceInstallationAnchorWriteState.Created,
            ServiceInstallationAnchorStore.WriteTo(_serviceDirectory, Doc()));
        byte[] before = File.ReadAllBytes(AnchorPath);

        ServiceInstallationAnchorWriteState conflicting =
            ServiceInstallationAnchorStore.WriteTo(_serviceDirectory, Doc(installationId, sid));

        Assert.Equal(ServiceInstallationAnchorWriteState.ConflictingIdentity, conflicting);
        Assert.Equal(before, File.ReadAllBytes(AnchorPath));
    }

    // ---- malformed / partially written state refusal ------------------------

    public static TheoryData<string> UnusableExistingStates() => new()
    {
        "",
        "   ",
        "{\"schemaVersion\": 1,",
        "{\"schemaVersion\": 1, \"productAnchorMarker\": \"PAXCookbook.ServiceInstallationAnchor.v1\"}",
        "{\"schemaVersion\": 2, \"productAnchorMarker\": \"PAXCookbook.ServiceInstallationAnchor.v1\","
            + " \"managedFeatureId\": \"service-installation-anchor\","
            + " \"installationId\": \"3f2504e0-4f89-41d3-9a0c-0305e82c3301\","
            + " \"initiatingUserSid\": \"S-1-5-21-1111111111-2222222222-3333333333-1001\","
            + " \"createdUtc\": \"2026-08-16T12:00:00Z\"}",
    };

    [Theory]
    [MemberData(nameof(UnusableExistingStates))]
    public void An_existing_unusable_anchor_is_refused_and_never_repaired_or_overwritten(string seeded)
    {
        SeedRawAnchor(seeded);
        byte[] before = File.ReadAllBytes(AnchorPath);

        Assert.Equal(
            ServiceInstallationAnchorReadState.Refused,
            ServiceInstallationAnchorStore.ReadFrom(_serviceDirectory).State);

        ServiceInstallationAnchorWriteState written =
            ServiceInstallationAnchorStore.WriteTo(_serviceDirectory, Doc());

        Assert.Equal(ServiceInstallationAnchorWriteState.ExistingStateRefused, written);
        Assert.Equal(before, File.ReadAllBytes(AnchorPath));
    }

    [Fact]
    public void An_oversized_file_at_the_anchor_path_is_refused_without_being_read()
    {
        SeedRawAnchor(new string('a', ServiceInstallationAnchorContract.MaxAnchorBytes + 1));

        ServiceInstallationAnchorReadResult read = ServiceInstallationAnchorStore.ReadFrom(_serviceDirectory);

        Assert.Equal(ServiceInstallationAnchorReadState.Refused, read.State);
        Assert.Equal(ServiceInstallationAnchorInvalidReason.OversizedInput, read.Validation!.Reason);
        Assert.Equal(
            ServiceInstallationAnchorWriteState.ExistingStateRefused,
            ServiceInstallationAnchorStore.WriteTo(_serviceDirectory, Doc()));
    }

    [Fact]
    public void A_byte_order_mark_is_refused_rather_than_silently_accepted()
    {
        Directory.CreateDirectory(_serviceDirectory);
        Assert.True(ServiceInstallationAnchorContract.TrySerialize(Doc(), out string json));
        File.WriteAllText(AnchorPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal(
            ServiceInstallationAnchorReadState.Refused,
            ServiceInstallationAnchorStore.ReadFrom(_serviceDirectory).State);
    }

    // ---- invalid proposal ---------------------------------------------------

    [Fact]
    public void An_invalid_proposal_is_refused_before_anything_is_created()
    {
        ServiceInstallationAnchorWriteState written = ServiceInstallationAnchorStore.WriteTo(
            _serviceDirectory, new ServiceInstallationAnchorDocument("not-a-guid", ClassicUserSid, "2026-08-16T12:00:00Z"));

        Assert.Equal(ServiceInstallationAnchorWriteState.InvalidProposal, written);
        Assert.False(Directory.Exists(_serviceDirectory));

        Assert.Equal(
            ServiceInstallationAnchorWriteState.InvalidProposal,
            ServiceInstallationAnchorStore.WriteTo(_serviceDirectory, null));
    }

    // ---- CYCLE 67R: THE MUTATION BOUNDARY IS Directory.CreateDirectory ------
    //
    // WHY THIS SECTION EXISTS. WriteTo calls Directory.CreateDirectory as the
    // FIRST statement of its write, BEFORE the temporary FileStream, the flush,
    // the atomic replace and the mandatory reread. Every failure at or after
    // that call therefore leaves the fixed machine root and service directories
    // durably present, with INHERITED access control and no anchor inside them -
    // which is exactly the residue shape preserved on the attended VM.
    //
    // Before this repair every one of those failures collapsed onto Unavailable,
    // which the channel maps to AnchorPersistenceRefused and the cycle-67
    // contract reports outward as refused_before_mutation. That is a positive,
    // load-bearing, BENIGN claim on a path where it is provably false, and it is
    // worse than silence: an operator who believes it retries, the next
    // attempt's mutation-free probe then observes the directories PRESENT, and
    // the compensation can never again claim it created them. The residue
    // becomes unremovable by design.

    /// <summary>
    /// Forces a failure strictly AFTER the one Directory.CreateDirectory call by
    /// occupying the fixed temporary leaf name with a DIRECTORY, so the
    /// temporary FileStream cannot be opened. Nothing is elevated, nothing is
    /// locked and no handle is held: the refusal is a property of the path
    /// shape, so the test is deterministic on any machine.
    /// </summary>
    private void BlockTheTemporaryWritePath() =>
        Directory.CreateDirectory(
            Path.Combine(_serviceDirectory, ServiceInstallationAnchorStore.TemporaryLeafFileName));

    [Fact]
    public void A_persistence_failure_after_the_directory_is_created_is_never_reported_as_untouched()
    {
        Directory.CreateDirectory(_serviceDirectory);
        BlockTheTemporaryWritePath();

        Assert.Equal(
            ServiceInstallationAnchorReadState.Absent,
            ServiceInstallationAnchorStore.ReadFrom(_serviceDirectory).State);

        ServiceInstallationAnchorWriteState written =
            ServiceInstallationAnchorStore.WriteTo(_serviceDirectory, Doc());

        // THE OBSERVED FACTS. The write failed, the anchor does not exist, and
        // the directory is still there.
        Assert.NotEqual(ServiceInstallationAnchorWriteState.Created, written);
        Assert.NotEqual(ServiceInstallationAnchorWriteState.AlreadyMatching, written);
        Assert.False(File.Exists(AnchorPath));
        Assert.True(Directory.Exists(_serviceDirectory));

        // THE LOAD-BEARING CLAIM. None of the three states the channel treats as
        // decided-before-any-create may be used to describe this, because each
        // of them is reported outward as refused_before_mutation.
        Assert.NotEqual(ServiceInstallationAnchorWriteState.Unavailable, written);
        Assert.NotEqual(ServiceInstallationAnchorWriteState.InvalidProposal, written);
        Assert.NotEqual(ServiceInstallationAnchorWriteState.ExistingStateRefused, written);
        Assert.NotEqual(ServiceInstallationAnchorWriteState.ConflictingIdentity, written);
        Assert.NotEqual(ServiceInstallationAnchorWriteState.Unspecified, written);
    }

    [Fact]
    public void Only_states_that_can_follow_the_create_are_allowed_to_admit_durable_directories()
    {
        // The predicate is the ONE place the mutation boundary is stated, and it
        // is total over the declared vocabulary.
        foreach (ServiceInstallationAnchorWriteState state
                 in Enum.GetValues<ServiceInstallationAnchorWriteState>())
        {
            bool mayHaveCreated = ServiceInstallationAnchorStore.MayHaveCreatedDirectories(state);

            switch (state)
            {
                case ServiceInstallationAnchorWriteState.Created:
                case ServiceInstallationAnchorWriteState.VerificationFailed:
                case ServiceInstallationAnchorWriteState.PersistenceFailedAfterCreate:
                    Assert.True(mayHaveCreated);
                    break;
                default:
                    Assert.False(mayHaveCreated);
                    break;
            }
        }

        // An undefined cast is never read as "nothing was mutated".
        Assert.True(
            ServiceInstallationAnchorStore.MayHaveCreatedDirectories(
                (ServiceInstallationAnchorWriteState)9999));
    }

    [Fact]
    public void A_refusal_decided_before_the_create_leaves_no_directory_behind()
    {
        // The control for the test above: a genuinely pre-create refusal really
        // does leave the filesystem untouched, so refused_before_mutation stays
        // truthful for the paths that earned it.
        Assert.Equal(
            ServiceInstallationAnchorWriteState.InvalidProposal,
            ServiceInstallationAnchorStore.WriteTo(_serviceDirectory, null));
        Assert.False(Directory.Exists(_serviceDirectory));

        Assert.False(
            ServiceInstallationAnchorStore.MayHaveCreatedDirectories(
                ServiceInstallationAnchorWriteState.InvalidProposal));
        Assert.False(
            ServiceInstallationAnchorStore.MayHaveCreatedDirectories(
                ServiceInstallationAnchorWriteState.Unavailable));
    }

    // ---- default results are never a success -------------------------------

    [Fact]
    public void A_default_read_result_is_unspecified_and_never_a_success()
    {
        var result = default(ServiceInstallationAnchorReadResult);

        Assert.Equal(ServiceInstallationAnchorReadState.Unspecified, result.State);
        Assert.Null(result.Validation);
        Assert.Equal("Unspecified", result.ToString());
        Assert.Equal(0, (int)default(ServiceInstallationAnchorWriteState));
    }
}
