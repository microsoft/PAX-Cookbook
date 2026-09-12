using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

// Cycle 42 - PROVIDER-SPECIFIC RIGHTS PROFILE BINDING (schema v2).
//
// WHAT THIS FILE PROVES. Exactly one rights profile is approved, and it is bound to
// the exact measured scope: Microsoft Software Key Storage Provider, RSA 2048,
// PS256, Azure.Identity 1.18.0, MSAL 4.82.1 and Microsoft.Graph.Authentication
// 2.39.0. The single approved mask is 0x00120009, DERIVED in the contract as
// ReadData | ReadExtendedAttributes | ReadPermissions | Synchronize. ReadAttributes
// (0x80) is NOT part of it, because the cycle-41e experiment measured it as not
// required.
//
// WHAT AN APPROVED PROFILE DOES NOT BUY. Nothing activates. Every raw ledger entry
// declaring lifecycleState "active" is still refused, including the entry that
// matches the approved provider, profile and mask exactly - that one now lands on a
// NEW terminal reason which says what is actually true: active-lifecycle acceptance
// is not authorised. ValidActive is still absent and outcome slot 2 is still
// retired. Promotion still emits zero actions.
//
// THE GENERIC-READ HAZARD THIS CYCLE CREATED, AND CLOSED. Before this cycle
// GenericRead (0x80000000) was refused only because NO profile existed at all:
// KnownRightsBits (0xF01F019B) INCLUDES the generic bits, so GenericRead passed the
// unknown-bit and prohibited-bit gates and died on the terminal brake. The moment a
// profile exists that brake stops covering it. GenericRead therefore has its OWN
// explicit bounded refusal, it is never normalised into the five-bit composite
// Windows would persist for it, and it is tested directly below.
//
// SCOPE. Nothing here opens a certificate store, touches a private key, reads or
// writes an ACL, reads the registry, installs or starts a service, elevates, opens
// a socket, spawns a process, runs PAX or starts a Bake. The only files read are
// project SOURCE files, as text, for structural containment.
public class ServiceOwnershipRightsProfileBindingTests
{
    // ---- synthetic fixture values -------------------------------------------

    private const string OwnerSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string SvcSid = "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890";
    private const string Thumb = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    private const string KeyId = "synthetic-key-identity_01.test";
    private const string Stamp = "2026-08-06T00:00:00Z";

    private const string ProviderToken = "microsoft-software-key-storage-provider";
    private const string RetiredProfileToken =
        "microsoft-software-ksp-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.0";
    private const string ProfileToken =
        "microsoft-software-ksp-backing-file-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.0-filesystemrights";
    private const string CngMechanismToken = "cng-security-descriptor";
    private const string CspMechanismToken = "csp-key-file-dacl";
    private const string BackingFileMechanismToken = "microsoft-software-ksp-backing-file-dacl";
    private const string DescriptorFormatToken = "microsoft-software-ksp-backing-file-self-relative-v1";
    private const string KeyStorageRootToken = "microsoft-software-key-storage-provider-machine-keys";
    private const string ProviderUniqueNameValue = "synthetic-unique-leaf_01.pvk";
    private const string ApprovedMaskText = "00120009";

    // Bound at DECLARATION binding, so the schema-v3 identity is demanded from the
    // contract before any method body in this file is compiled.
    private const int PinnedSchemaVersion = ServiceOwnershipLedgerContract.LedgerSchemaVersion;
    private const int PinnedPolicyVersion = ServiceOwnershipLedgerContract.RightsPolicyVersion;
    private const uint PinnedApprovedMask = ServiceOwnershipLedgerContract.ApprovedRightsProfileMask;

    // ===========================================================================
    // SCHEMA AND POLICY VERSION
    // ===========================================================================

    [Fact]
    public void The_schema_and_rights_policy_versions_are_exactly_three()
    {
        Assert.Equal(3, PinnedSchemaVersion);
        Assert.Equal(3, PinnedPolicyVersion);
        Assert.Equal(3, ServiceOwnershipLedgerContract.LedgerSchemaVersion);
        Assert.Equal(3, ServiceOwnershipLedgerContract.RightsPolicyVersion);
    }

    [Fact]
    public void Schema_version_one_is_rejected_outright_with_no_migration_and_no_fallback()
    {
        // Schema v1 is PERMANENTLY retired. This is not the "unsupported version
        // number that might later be supported" anti-pattern: v1 can never come
        // back, because the entry shape it describes cannot express the required
        // rightsProfileId property at all.
        ServiceOwnershipLedgerValidationResult v1 =
            ServiceOwnershipLedgerValidator.Validate(EmptyDocument(schemaVersion: "1"));

        Assert.True(v1.IsRefused);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.UnsupportedSchemaVersion, v1.Reason);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Unsupported, v1.Outcome);
        Assert.Null(v1.Document);

        // POSITIVE CONTROL: the same document at schema v3 IS accepted, so the
        // refusal above is about the version and not about a broken fixture.
        ServiceOwnershipLedgerValidationResult v2 =
            ServiceOwnershipLedgerValidator.Validate(EmptyDocument(schemaVersion: "3"));
        Assert.True(v2.IsAccepted);
        Assert.Equal(ServiceOwnershipLedgerOutcome.ValidEmpty, v2.Outcome);
    }

    [Fact]
    public void Schema_version_two_is_rejected_outright_with_no_migration_and_no_fallback()
    {
        // Schema v2 is PERMANENTLY retired for the same kind of structural reason as v1: it
        // permitted the CNG key-object descriptor mechanism that schema v3 retires as
        // unauthorizable. This is a permanent-retirement contract, not the "version number
        // that might later be supported" anti-pattern, so it is pinned separately.
        ServiceOwnershipLedgerValidationResult retired =
            ServiceOwnershipLedgerValidator.Validate(EmptyDocument(schemaVersion: "2"));

        Assert.True(retired.IsRefused);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.UnsupportedSchemaVersion, retired.Reason);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Unsupported, retired.Outcome);
        Assert.Null(retired.Document);

        // POSITIVE CONTROL: the same document at schema v3 IS accepted, so the refusal above
        // is about the version and not about a broken fixture.
        ServiceOwnershipLedgerValidationResult supported =
            ServiceOwnershipLedgerValidator.Validate(EmptyDocument(schemaVersion: "3"));
        Assert.True(supported.IsAccepted);
        Assert.Equal(ServiceOwnershipLedgerOutcome.ValidEmpty, supported.Outcome);
    }

    [Fact]
    public void A_schema_v1_shaped_entry_without_the_profile_property_cannot_be_repaired_by_version_alone()
    {
        // The v1 entry shape - correct in every way except that it predates
        // rightsProfileId - is refused even when it claims schema v2.
        DocFixture doc = PopulatedDocument();
        doc.Entry.OmitProperty = "rightsProfileId";

        ServiceOwnershipLedgerValidationResult result =
            ServiceOwnershipLedgerValidator.Validate(Build(doc));

        Assert.True(result.IsRefused);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.MissingProperty, result.Reason);
    }

    // ===========================================================================
    // PROVIDER AND PROFILE TOKENS
    // ===========================================================================

    [Fact]
    public void The_exact_provider_kind_and_profile_identifier_round_trip_through_their_wire_tokens()
    {
        Assert.Equal(
            ProviderToken,
            ServiceOwnershipLedgerContract.ToWireToken(
                ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider));

        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            ProviderToken, out ServiceOwnershipPrivateKeyProviderKind provider));
        Assert.Equal(
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            provider);

        Assert.Equal(
            ProfileToken,
            ServiceOwnershipLedgerContract.ToWireToken(
                ServiceOwnershipRightsProfileId
                    .MicrosoftSoftwareKspBackingFileRsa2048Ps256AzureIdentity1_18_0Msal4_82_1GraphAuth2_39_0FileSystemRights));

        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            ProfileToken, out ServiceOwnershipRightsProfileId profile));
        Assert.Equal(
            ServiceOwnershipRightsProfileId
                .MicrosoftSoftwareKspBackingFileRsa2048Ps256AzureIdentity1_18_0Msal4_82_1GraphAuth2_39_0FileSystemRights,
            profile);

        // The approved identifiers are the ones the round trip produced.
        Assert.Equal(ServiceOwnershipLedgerContract.ApprovedRightsProfileId, profile);
        Assert.Equal(ServiceOwnershipLedgerContract.ApprovedRightsProfileProviderKind, provider);
        Assert.Equal(
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileGrantMechanism);
    }

    [Fact]
    public void The_profile_vocabulary_is_closed_at_unspecified_plus_a_retired_and_an_approved_member()
    {
        string[] names = Enum.GetNames(typeof(ServiceOwnershipRightsProfileId));
        Assert.Equal(3, names.Length);
        Assert.Contains("Unspecified", names);
        Assert.Equal(0, (int)ServiceOwnershipRightsProfileId.Unspecified);
        Assert.Equal(2, (int)ServiceOwnershipLedgerContract.ApprovedRightsProfileId);

        // The zero value has NO wire token, so Unspecified can never be written or
        // read back.
        Assert.Equal(
            string.Empty,
            ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipRightsProfileId.Unspecified));

        // The identifier is not generic: it names the measured scope.
        foreach (string fragment in new[]
                 {
                     "microsoft-software-ksp-backing-file", "rsa2048", "ps256",
                     "azure-identity-1.18.0", "msal-4.82.1", "graph-auth-2.39.0", "filesystemrights",
                 })
        {
            Assert.Contains(fragment, ProfileToken, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_broad_provider_classifications_keep_their_own_distinct_tokens()
    {
        Assert.Equal("cng", ServiceOwnershipLedgerContract.ToWireToken(
            ServiceOwnershipPrivateKeyProviderKind.Cng));
        Assert.Equal("legacy-csp", ServiceOwnershipLedgerContract.ToWireToken(
            ServiceOwnershipPrivateKeyProviderKind.LegacyCsp));

        Assert.NotEqual("cng", ProviderToken);
        Assert.NotEqual("legacy-csp", ProviderToken);

        string[] names = Enum.GetNames(typeof(ServiceOwnershipPrivateKeyProviderKind));
        Assert.Equal(4, names.Length);
        Assert.Equal(
            new[] { "Unspecified", "Cng", "LegacyCsp", "MicrosoftSoftwareKeyStorageProvider" },
            names.OrderBy(n => (int)Enum.Parse<ServiceOwnershipPrivateKeyProviderKind>(n)).ToArray());
    }

    [Theory]
    [InlineData("microsoft-software-key-storage-provider ")]
    [InlineData("Microsoft-Software-Key-Storage-Provider")]
    [InlineData("microsoft-software-ksp")]
    [InlineData("mskeystorageprovider")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_provider_token_fails_closed(string? token)
    {
        Assert.False(ServiceOwnershipLedgerContract.TryParseWireToken(
            token, out ServiceOwnershipPrivateKeyProviderKind parsed));
        Assert.Equal(ServiceOwnershipPrivateKeyProviderKind.Unspecified, parsed);

        DocFixture doc = PopulatedDocument();
        doc.Entry.Provider = token ?? string.Empty;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidProviderKind);
    }

    [Theory]
    [InlineData("microsoft-software-ksp-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.1")]
    [InlineData("microsoft-software-ksp-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1")]
    [InlineData("microsoft-software-ksp-rsa4096-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.0")]
    [InlineData("cng")]
    [InlineData("default")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_profile_token_fails_closed(string? token)
    {
        Assert.False(ServiceOwnershipLedgerContract.TryParseWireToken(
            token, out ServiceOwnershipRightsProfileId parsed));
        Assert.Equal(ServiceOwnershipRightsProfileId.Unspecified, parsed);

        DocFixture doc = PopulatedDocument();
        doc.Entry.Profile = token ?? string.Empty;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidRightsProfileId);
    }

    // ===========================================================================
    // THE APPROVED MASK IS DERIVED, NOT TYPED
    // ===========================================================================

    [Fact]
    public void The_approved_mask_is_exactly_the_or_of_the_four_required_rights()
    {
        uint derived =
            ServiceOwnershipLedgerContract.ReadDataRight
            | ServiceOwnershipLedgerContract.ReadExtendedAttributesRight
            | ServiceOwnershipLedgerContract.ReadPermissionsRight
            | ServiceOwnershipLedgerContract.SynchronizeRight;

        Assert.Equal(derived, PinnedApprovedMask);
        Assert.Equal(0x00120009u, PinnedApprovedMask);
        Assert.Equal(ApprovedMaskText, new ServiceOwnershipRightsMask(PinnedApprovedMask).ToWireText());

        // Exactly four bits, and each of the four is individually present.
        Assert.Equal(4, System.Numerics.BitOperations.PopCount(PinnedApprovedMask));
        foreach (uint bit in new[]
                 {
                     ServiceOwnershipLedgerContract.ReadDataRight,
                     ServiceOwnershipLedgerContract.ReadExtendedAttributesRight,
                     ServiceOwnershipLedgerContract.ReadPermissionsRight,
                     ServiceOwnershipLedgerContract.SynchronizeRight,
                 })
        {
            Assert.Equal(bit, PinnedApprovedMask & bit);
        }
    }

    [Fact]
    public void Read_attributes_is_absent_from_the_approved_mask()
    {
        // Measured, not assumed: cycle 41e ran all four immediate strict subsets of
        // 0x00120009 four times each with zero successes, and 0x00120089 - the same
        // mask plus ReadAttributes - was a strict SUPERSET success, never a minimum.
        Assert.Equal(0x00000080u, ServiceOwnershipLedgerContract.ReadAttributesRight);
        Assert.Equal(
            0u,
            PinnedApprovedMask & ServiceOwnershipLedgerContract.ReadAttributesRight);
        Assert.NotEqual(0x00120089u, PinnedApprovedMask);
    }

    [Fact]
    public void The_approved_mask_is_kept_separate_from_the_known_and_prohibited_sets()
    {
        // Ruling B1.3 from cycle 38a still holds: known, prohibited and approved are
        // THREE things. The approved profile is a strict subset of the known bits and
        // shares nothing with the prohibited or not-used bits.
        Assert.Equal(
            PinnedApprovedMask,
            PinnedApprovedMask & ServiceOwnershipLedgerContract.KnownRightsBits);
        Assert.NotEqual(PinnedApprovedMask, ServiceOwnershipLedgerContract.KnownRightsBits);
        Assert.Equal(
            0u,
            PinnedApprovedMask & ServiceOwnershipLedgerContract.ProhibitedManagementRightsBits);
        Assert.Equal(
            0u,
            PinnedApprovedMask & ServiceOwnershipLedgerContract.NotUsedRightsBits);
        Assert.Equal(
            0u,
            PinnedApprovedMask & ServiceOwnershipLedgerContract.GenericReadRight);

        // An approved profile now EXISTS - and that is the only thing this flag says.
        Assert.True(ServiceOwnershipLedgerContract.HasApprovedRightsProfile);
    }

    // ===========================================================================
    // THE AUTHORIZER - THE ONE ACCEPTING CASE
    // ===========================================================================

    private static bool Authorize(
        uint mask,
        ServiceOwnershipPrivateKeyProviderKind kind,
        ServiceOwnershipGrantMechanism mechanism,
        ServiceOwnershipRightsProfileId profile,
        int policyVersion,
        out ServiceOwnershipLedgerInvalidReason reason) =>
        Authorize(mask, kind, mechanism, profile, policyVersion,
            ServiceOwnershipLedgerContract.ApprovedDescriptorFormat, out reason);

    private static bool Authorize(
        uint mask,
        ServiceOwnershipPrivateKeyProviderKind kind,
        ServiceOwnershipGrantMechanism mechanism,
        ServiceOwnershipRightsProfileId profile,
        int policyVersion,
        ServiceOwnershipDescriptorFormat descriptorFormat,
        out ServiceOwnershipLedgerInvalidReason reason) =>
        ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
            new ServiceOwnershipRightsMask(mask), kind, mechanism, profile, policyVersion, descriptorFormat, out reason);

    private static bool AuthorizeExact(uint mask, out ServiceOwnershipLedgerInvalidReason reason) =>
        Authorize(
            mask,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            3,
            out reason);

    [Fact]
    public void The_exact_provider_profile_mechanism_version_and_mask_are_authorised()
    {
        Assert.True(AuthorizeExact(
            PinnedApprovedMask, out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, reason);
    }

    [Fact]
    public void Exactly_one_subset_of_the_fourteen_specific_rights_is_authorised()
    {
        uint[] specific = ServiceOwnershipLedgerContract.SpecificFileRightsBits.ToArray();
        Assert.Equal(14, specific.Length);

        var authorised = new List<uint>();
        int refused = 0;

        for (int subset = 0; subset < (1 << 14); subset++)
        {
            uint value = 0u;
            for (int bit = 0; bit < specific.Length; bit++)
            {
                if ((subset & (1 << bit)) != 0)
                {
                    value |= specific[bit];
                }
            }

            if (AuthorizeExact(value, out ServiceOwnershipLedgerInvalidReason reason))
            {
                Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, reason);
                authorised.Add(value);
            }
            else
            {
                Assert.NotEqual(ServiceOwnershipLedgerInvalidReason.None, reason);
                refused++;
            }
        }

        Assert.Equal(16384, authorised.Count + refused);
        uint only = Assert.Single(authorised);
        Assert.Equal(0x00120009u, only);
        Assert.Equal(16383, refused);
    }

    [Fact]
    public void Every_omission_of_a_required_bit_is_refused_by_name()
    {
        foreach (uint bit in new[]
                 {
                     ServiceOwnershipLedgerContract.ReadDataRight,
                     ServiceOwnershipLedgerContract.ReadExtendedAttributesRight,
                     ServiceOwnershipLedgerContract.ReadPermissionsRight,
                     ServiceOwnershipLedgerContract.SynchronizeRight,
                 })
        {
            uint reduced = PinnedApprovedMask & ~bit;
            Assert.NotEqual(PinnedApprovedMask, reduced);

            Assert.False(
                AuthorizeExact(reduced, out ServiceOwnershipLedgerInvalidReason reason),
                bit.ToString("X8", CultureInfo.InvariantCulture));
            Assert.Equal(
                ServiceOwnershipLedgerInvalidReason.ApprovedRightsProfileBitMissing,
                reason);
        }
    }

    /// <summary>
    /// Every published bit that is NOT part of the approved profile, paired with the
    /// EXACT bounded reason adding it must produce. There are no supersets: adding
    /// anything at all is refused, and the refusal says which class of bit it was.
    /// </summary>
    public static TheoryData<uint, ServiceOwnershipLedgerInvalidReason> NonProfileKnownBits() => new()
    {
        { 0x00000002u, ServiceOwnershipLedgerInvalidReason.ProhibitedManagementRight },            // WriteData
        { 0x00000010u, ServiceOwnershipLedgerInvalidReason.ProhibitedManagementRight },            // WriteExtendedAttributes
        { 0x00000080u, ServiceOwnershipLedgerInvalidReason.ApprovedRightsProfileBitNotPermitted }, // ReadAttributes
        { 0x00000100u, ServiceOwnershipLedgerInvalidReason.ProhibitedManagementRight },            // WriteAttributes
        { 0x00010000u, ServiceOwnershipLedgerInvalidReason.ProhibitedManagementRight },            // Delete
        { 0x00040000u, ServiceOwnershipLedgerInvalidReason.ProhibitedManagementRight },            // ChangePermissions
        { 0x00080000u, ServiceOwnershipLedgerInvalidReason.ProhibitedManagementRight },            // TakeOwnership
        { 0x10000000u, ServiceOwnershipLedgerInvalidReason.ProhibitedManagementRight },            // GenericAll
        { 0x20000000u, ServiceOwnershipLedgerInvalidReason.ProhibitedManagementRight },            // GenericExecute
        { 0x40000000u, ServiceOwnershipLedgerInvalidReason.ProhibitedManagementRight },            // GenericWrite
        { 0x80000000u, ServiceOwnershipLedgerInvalidReason.GenericReadRightNotAuthorized },        // GenericRead
    };

    [Theory]
    [MemberData(nameof(NonProfileKnownBits))]
    public void Adding_any_known_bit_outside_the_profile_is_refused_by_its_own_reason(
        uint bit,
        ServiceOwnershipLedgerInvalidReason expected)
    {
        // The bit really is published, and really is outside the profile.
        Assert.Equal(bit, bit & ServiceOwnershipLedgerContract.FileKnownRightsBits);
        Assert.Equal(0u, bit & PinnedApprovedMask);

        Assert.False(
            AuthorizeExact(PinnedApprovedMask | bit, out ServiceOwnershipLedgerInvalidReason reason),
            bit.ToString("X8", CultureInfo.InvariantCulture));
        Assert.Equal(expected, reason);

        // The bit ALONE is refused too, so nothing is authorised by accompaniment.
        Assert.False(AuthorizeExact(bit, out ServiceOwnershipLedgerInvalidReason alone));
        Assert.NotEqual(ServiceOwnershipLedgerInvalidReason.None, alone);
    }

    [Theory]
    [InlineData(0x00200000u)]
    [InlineData(0x00400000u)]
    [InlineData(0x00800000u)]
    [InlineData(0x08000000u)]
    public void Adding_an_unpublished_bit_is_refused_as_unknown(uint bit)
    {
        Assert.Equal(0u, bit & ServiceOwnershipLedgerContract.FileKnownRightsBits);

        Assert.False(AuthorizeExact(
            PinnedApprovedMask | bit, out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.UnknownRightsBit, reason);
    }

    [Fact]
    public void A_zero_mask_is_refused_as_zero_rather_than_as_a_missing_profile_bit()
    {
        Assert.False(AuthorizeExact(0u, out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.ZeroRightsMask, reason);
    }

    // ---- GENERIC READ, tested directly and never normalised -------------------

    [Fact]
    public void Generic_read_is_refused_by_its_own_reason_and_is_never_normalised()
    {
        // Windows PERSISTS a requested GenericRead ACE as the five-bit composite
        // 0x00120089 (cycle 41e). The contract must NOT perform that normalisation:
        // 0x80000000 has to be refused as GenericRead, not silently rewritten into a
        // superset of the approved profile and then judged.
        Assert.False(AuthorizeExact(
            ServiceOwnershipLedgerContract.GenericReadRight,
            out ServiceOwnershipLedgerInvalidReason alone));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.GenericReadRightNotAuthorized, alone);

        Assert.False(AuthorizeExact(
            PinnedApprovedMask | ServiceOwnershipLedgerContract.GenericReadRight,
            out ServiceOwnershipLedgerInvalidReason combined));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.GenericReadRightNotAuthorized, combined);

        // The composite Windows would have stored is ALSO refused - as a superset,
        // by the not-permitted reason - so neither spelling can activate.
        Assert.False(AuthorizeExact(0x00120089u, out ServiceOwnershipLedgerInvalidReason composite));
        Assert.Equal(
            ServiceOwnershipLedgerInvalidReason.ApprovedRightsProfileBitNotPermitted,
            composite);

        // GenericRead is refused BEFORE the unknown-bit and prohibited-bit gates,
        // so its own reason cannot be masked by an accompanying bit.
        Assert.False(AuthorizeExact(
            ServiceOwnershipLedgerContract.GenericReadRight | 0x00000004u,
            out ServiceOwnershipLedgerInvalidReason withUnknown));
        Assert.Equal(
            ServiceOwnershipLedgerInvalidReason.GenericReadRightNotAuthorized,
            withUnknown);

        Assert.False(AuthorizeExact(
            ServiceOwnershipLedgerContract.GenericReadRight
                | ServiceOwnershipLedgerContract.DeleteRight,
            out ServiceOwnershipLedgerInvalidReason withProhibited));
        Assert.Equal(
            ServiceOwnershipLedgerInvalidReason.GenericReadRightNotAuthorized,
            withProhibited);

        // Unsigned throughout: GenericRead is NEGATIVE as Int32.
        Assert.True(ServiceOwnershipLedgerContract.GenericReadRight > int.MaxValue);
        Assert.Equal(
            "80000000",
            new ServiceOwnershipRightsMask(ServiceOwnershipLedgerContract.GenericReadRight).ToWireText());
    }

    // ---- PROVIDER, PROFILE, MECHANISM AND VERSION MISMATCHES ------------------

    [Fact]
    public void Broad_cng_cannot_inherit_the_profile()
    {
        Assert.False(Authorize(
            PinnedApprovedMask,
            ServiceOwnershipPrivateKeyProviderKind.Cng,
            ServiceOwnershipGrantMechanism.CngSecurityDescriptor,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            3,
            out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(
            ServiceOwnershipLedgerInvalidReason.RetiredCngSecurityDescriptorMechanismUnsupported,
            reason);

        Assert.False(ServiceOwnershipLedgerContract.IsExactApprovedProviderKind(
            ServiceOwnershipPrivateKeyProviderKind.Cng));
        Assert.False(ServiceOwnershipLedgerContract.IsApprovedRightsProfile(
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            ServiceOwnershipPrivateKeyProviderKind.Cng));
    }

    [Fact]
    public void Legacy_csp_cannot_inherit_the_profile()
    {
        Assert.False(Authorize(
            PinnedApprovedMask,
            ServiceOwnershipPrivateKeyProviderKind.LegacyCsp,
            ServiceOwnershipGrantMechanism.CspKeyFileDacl,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            3,
            out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(
            ServiceOwnershipLedgerInvalidReason.UnsupportedProviderRightsVocabulary,
            reason);

        Assert.False(ServiceOwnershipLedgerContract.IsRightsVocabularySupported(
            ServiceOwnershipPrivateKeyProviderKind.LegacyCsp));
        Assert.False(ServiceOwnershipLedgerContract.IsApprovedRightsProfile(
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            ServiceOwnershipPrivateKeyProviderKind.LegacyCsp));
    }

    [Fact]
    public void A_wrong_mechanism_is_refused_before_anything_else()
    {
        Assert.False(Authorize(
            PinnedApprovedMask,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.CspKeyFileDacl,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            3,
            out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.ProviderMechanismMismatch, reason);

        Assert.True(ServiceOwnershipLedgerContract.IsMechanismPairedWith(
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl));
        Assert.False(ServiceOwnershipLedgerContract.IsMechanismPairedWith(
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.CspKeyFileDacl));
    }

    [Fact]
    public void A_missing_profile_is_refused_as_no_approved_profile()
    {
        Assert.False(Authorize(
            PinnedApprovedMask,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            ServiceOwnershipRightsProfileId.Unspecified,
            3,
            out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.NoApprovedRightsProfile, reason);
    }

    [Fact]
    public void An_undefined_profile_value_is_refused_as_a_provider_profile_mismatch()
    {
        // A value outside the declared vocabulary cannot be the approved profile.
        Assert.False(Authorize(
            PinnedApprovedMask,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            (ServiceOwnershipRightsProfileId)9999,
            3,
            out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.ProviderProfileMismatch, reason);
        Assert.False(Enum.IsDefined(typeof(ServiceOwnershipRightsProfileId), 9999));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(2147483647)]
    public void A_wrong_rights_policy_version_is_refused(int version)
    {
        Assert.NotEqual(ServiceOwnershipLedgerContract.RightsPolicyVersion, version);

        Assert.False(Authorize(
            PinnedApprovedMask,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            version,
            out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.InvalidRightsPolicyVersion, reason);
    }

    // ===========================================================================
    // ACTIVE LIFECYCLE IS ACCEPTED ONLY ON THE EXACT PROFILE (cycle 82, D1)
    // ===========================================================================

    [Fact]
    public void The_exact_profile_active_entry_is_accepted_and_every_other_active_entry_is_not()
    {
        // The exact profile at a NON-done transaction is still ACCEPTED by the parser
        // (the entry gates are lifecycle-independent), but it is classified InProgress
        // rather than Active, and the lifecycle planner treats the pair as incoherent.
        DocFixture doc = PopulatedDocument();
        doc.Entry.Lifecycle = "active";
        doc.TransactionState = "credential-mutated";

        ServiceOwnershipLedgerValidationResult result =
            ServiceOwnershipLedgerValidator.Validate(Build(doc));

        Assert.True(result.IsAccepted);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, result.Reason);
        Assert.Equal(ServiceOwnershipLedgerOutcome.InProgress, result.Outcome);
        Assert.NotNull(result.Document);

        // The SAME entry under a done transaction is the completed-promotion shape.
        DocFixture done = PopulatedDocument();
        done.Entry.Lifecycle = "active";
        done.TransactionState = "done";
        ServiceOwnershipLedgerValidationResult active =
            ServiceOwnershipLedgerValidator.Validate(Build(done));
        Assert.True(active.IsAccepted);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Active, active.Outcome);

        // NEGATIVE CONTROL: one field off the exact profile and the same active entry
        // is refused, so acceptance is about the exact profile and not about the
        // lifecycle being waved through.
        DocFixture nearMiss = PopulatedDocument();
        nearMiss.Entry.Lifecycle = "active";
        nearMiss.TransactionState = "done";
        nearMiss.Entry.Mask = "00120089";
        ServiceOwnershipLedgerValidationResult refused =
            ServiceOwnershipLedgerValidator.Validate(Build(nearMiss));
        Assert.True(refused.IsRefused);
        Assert.Null(refused.Document);
    }

    [Fact]
    public void Every_raw_active_entry_is_refused_across_provider_mask_and_transaction_state_except_the_exact_profile()
    {
        var providers = new[]
        {
            (Provider: ProviderToken, Mechanism: BackingFileMechanismToken),
            (Provider: ProviderToken, Mechanism: CngMechanismToken),
            (Provider: ProviderToken, Mechanism: CspMechanismToken),
            (Provider: "cng", Mechanism: CngMechanismToken),
            (Provider: "cng", Mechanism: CspMechanismToken),
            (Provider: "legacy-csp", Mechanism: CspMechanismToken),
        };

        string[] masks =
        {
            ApprovedMaskText, "00120089", "00000001", "00000081", "80000000", "00100000",
        };

        string[] transactions = { "preparing", "credential-mutated", "ledger-committed", "restoring", "done" };

        int cases = 0;
        int acceptedCases = 0;

        foreach ((string provider, string mechanism) in providers)
        {
            foreach (string mask in masks)
            {
                foreach (string transaction in transactions)
                {
                    DocFixture doc = PopulatedDocument();
                    doc.TransactionState = transaction;
                    doc.Entry.Provider = provider;
                    doc.Entry.Mechanism = mechanism;
                    doc.Entry.Mask = mask;
                    doc.Entry.Lifecycle = "active";

                    ServiceOwnershipLedgerValidationResult result =
                        ServiceOwnershipLedgerValidator.Validate(Build(doc));

                    bool exactProfile = provider == ProviderToken
                        && mechanism == BackingFileMechanismToken
                        && mask == ApprovedMaskText;

                    if (exactProfile)
                    {
                        Assert.True(result.IsAccepted, provider + "/" + mechanism + "/" + mask + "/" + transaction);
                        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, result.Reason);
                        Assert.Equal(
                            transaction == "done"
                                ? ServiceOwnershipLedgerOutcome.Active
                                : ServiceOwnershipLedgerOutcome.InProgress,
                            result.Outcome);
                        acceptedCases++;
                    }
                    else
                    {
                        Assert.True(result.IsRefused, provider + "/" + mechanism + "/" + mask + "/" + transaction);
                        Assert.Null(result.Document);
                        Assert.NotEqual(ServiceOwnershipLedgerInvalidReason.None, result.Reason);
                    }

                    cases++;
                }
            }
        }

        Assert.Equal(6 * 6 * 5, cases);

        // EXACTLY the five transaction states of the ONE exact profile are accepted.
        // 175 of 180 active entries remain refused.
        Assert.Equal(5, acceptedCases);
    }

    [Fact]
    public void The_retired_active_success_name_stays_gone_and_slot_two_is_still_retired()
    {
        // Cycle 82 authorized active acceptance at the NEW value 9. The RETIRED
        // slot-2 name must still not come back, and slot 2 itself must stay empty.
        string[] outcomes = Enum.GetNames(typeof(ServiceOwnershipLedgerOutcome));
        Assert.DoesNotContain("ValidActive", outcomes);
        Assert.Contains("Active", outcomes);
        Assert.Equal(9, (int)ServiceOwnershipLedgerOutcome.Active);
        Assert.False(Enum.IsDefined(typeof(ServiceOwnershipLedgerOutcome), 2));

        int[] declared = Enum.GetValues(typeof(ServiceOwnershipLedgerOutcome))
            .Cast<ServiceOwnershipLedgerOutcome>()
            .Select(v => (int)v)
            .OrderBy(v => v)
            .ToArray();
        Assert.Equal(new[] { 0, 1, 3, 4, 5, 6, 7, 8, 9 }, declared);

        // The REFUSAL mapping did not broaden: no refusal reason may map to any
        // accepted outcome, and in particular none may map to the new Active value.
        foreach (ServiceOwnershipLedgerInvalidReason reason in
                 Enum.GetValues<ServiceOwnershipLedgerInvalidReason>())
        {
            ServiceOwnershipLedgerOutcome mapped =
                ServiceOwnershipLedgerContract.MapRefusalOutcome(reason);
            Assert.Contains(mapped, new[]
            {
                ServiceOwnershipLedgerOutcome.Malformed,
                ServiceOwnershipLedgerOutcome.Unsupported,
                ServiceOwnershipLedgerOutcome.Foreign,
                ServiceOwnershipLedgerOutcome.Inconsistent,
            });
            Assert.NotEqual(ServiceOwnershipLedgerOutcome.Active, mapped);
        }
    }

    [Fact]
    public void Promotion_still_plans_zero_actions_with_an_approved_profile_present()
    {
        Assert.True(ServiceOwnershipLedgerContract.HasApprovedRightsProfile);

        var ledgers = new List<ServiceOwnershipLedgerValidationResult>
        {
            ServiceOwnershipLedgerValidator.ForAbsentLedger(),
            ServiceOwnershipLedgerValidator.Validate(EmptyDocument("3")),
            ServiceOwnershipLedgerValidator.Validate(Build(PopulatedDocument())),
        };

        foreach (ServiceOwnershipLedgerValidationResult ledger in ledgers)
        {
            ServiceOwnershipLifecyclePlanRequest? request =
                ServiceOwnershipLifecyclePlanRequest.ForAssessPromotion(
                    ledger, "install-0001", OwnerSid, SvcSid);
            Assert.NotNull(request);

            ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(request);

            Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
            Assert.Empty(plan.Actions);
            Assert.Contains(
                plan.RefusalReason,
                new[]
                {
                    ServiceOwnershipPlanRefusalReason.PromotionNotAuthorized,
                    ServiceOwnershipPlanRefusalReason.LedgerRefused,
                });
        }
    }

    // ===========================================================================
    // CAPTURED-STATE BINDING - v3 PREIMAGE
    // ===========================================================================

    private const string VectorOwnerSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string VectorServiceSid = "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890";
    private const string VectorThumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    private const string VectorKeyIdentity = "synthetic-key-identity_01.test";
    private const string VectorMask = "00120009";
    private const string VectorPriorSha256 =
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const int VectorGeneration = 7;
    private const string VectorProviderUniqueName = "synthetic-unique-leaf_01.pvk";

    private const string VectorExpectedPreimage =
        "PAXCookbook.ServiceOwnership.Binding.v3\n" +
        "generation=7\n" +
        "owningUserSid=S-1-5-21-1111111111-2222222222-3333333333-1001\n" +
        "serviceSid=S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890\n" +
        "certificateThumbprintSha1=ABCDEF0123456789ABCDEF0123456789ABCDEF01\n" +
        "privateKeyProviderKind=microsoft-software-key-storage-provider\n" +
        "rightsProfileId=microsoft-software-ksp-backing-file-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.0-filesystemrights\n" +
        "keyIdentity=synthetic-key-identity_01.test\n" +
        "grantMechanism=microsoft-software-ksp-backing-file-dacl\n" +
        "grantedRightsMask=00120009\n" +
        "priorDaclState=present\n" +
        "priorDaclSha256=0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF\n" +
        "providerUniqueName=synthetic-unique-leaf_01.pvk\n" +
        "keyStorageRoot=microsoft-software-key-storage-provider-machine-keys\n" +
        "descriptorFormat=microsoft-software-ksp-backing-file-self-relative-v1";

    [Fact]
    public void The_fixed_schema_v3_binding_vector_is_re_derived_here_rather_than_copied()
    {
        // THE POINT OF THIS TEST. The expected digest is computed IN THIS TEST from a
        // preimage this file spells out character by character. Nothing is copied
        // from the contract's output, so the contract cannot define its own vector.
        byte[] independentBytes = new UTF8Encoding(false).GetBytes(VectorExpectedPreimage);
        string independentDigest = Hex(SHA256.HashData(independentBytes));

        Assert.Equal(15, VectorExpectedPreimage.Split('\n').Length);
        Assert.Equal(869, independentBytes.Length);
        Assert.DoesNotContain("\r", VectorExpectedPreimage, StringComparison.Ordinal);
        Assert.False(VectorExpectedPreimage.EndsWith("\n", StringComparison.Ordinal));
        Assert.False(
            independentBytes.Length >= 3
            && independentBytes[0] == 0xEF && independentBytes[1] == 0xBB && independentBytes[2] == 0xBF);

        string contractPreimage = ServiceOwnershipLedgerContract.BuildCapturedStateBindingPreimage(
            VectorGeneration,
            VectorOwnerSid,
            VectorServiceSid,
            VectorThumbprint,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            VectorKeyIdentity,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            VectorMask,
            ServiceOwnershipPriorDaclState.Present,
            VectorPriorSha256,
            VectorProviderUniqueName,
            ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
            ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1);

        Assert.Equal(VectorExpectedPreimage, contractPreimage);

        string contractDigest = ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            VectorGeneration,
            VectorOwnerSid,
            VectorServiceSid,
            VectorThumbprint,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            VectorKeyIdentity,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            VectorMask,
            ServiceOwnershipPriorDaclState.Present,
            VectorPriorSha256,
            VectorProviderUniqueName,
            ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
            ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1);

        Assert.Equal(independentDigest, contractDigest);
        Assert.Equal(64, contractDigest.Length);
        Assert.Equal(contractDigest.ToUpperInvariant(), contractDigest);

        // NEGATIVE CONTROL: a one-character change to the preimage changes the
        // digest, so the equality above is not a comparison of two constants that
        // happen to be equal for a trivial reason.
        string mutated = VectorExpectedPreimage.Replace("generation=7", "generation=8", StringComparison.Ordinal);
        Assert.NotEqual(VectorExpectedPreimage, mutated);
        Assert.NotEqual(
            independentDigest,
            Hex(SHA256.HashData(new UTF8Encoding(false).GetBytes(mutated))));
    }

    [Fact]
    public void The_binding_prefix_is_version_three_and_the_field_order_carries_the_new_fields()
    {
        Assert.Equal(
            "PAXCookbook.ServiceOwnership.Binding.v3",
            ServiceOwnershipLedgerContract.BindingPreimagePrefix);
        Assert.DoesNotContain(
            "Binding.v1",
            ServiceOwnershipLedgerContract.BindingPreimagePrefix,
            StringComparison.Ordinal);

        Assert.Equal(
            new[]
            {
                "generation", "owningUserSid", "serviceSid", "certificateThumbprintSha1",
                "privateKeyProviderKind", "rightsProfileId", "keyIdentity", "grantMechanism",
                "grantedRightsMask", "priorDaclState", "priorDaclSha256",
                "providerUniqueName", "keyStorageRoot", "descriptorFormat",
            },
            ServiceOwnershipLedgerContract.BindingFieldOrder.ToArray());

        // The declared field order really is the order the preimage emits.
        string[] emitted = ServiceOwnershipLedgerContract.BuildCapturedStateBindingPreimage(
                VectorGeneration, VectorOwnerSid, VectorServiceSid, VectorThumbprint,
                ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
                ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
                VectorKeyIdentity, ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
                VectorMask, ServiceOwnershipPriorDaclState.Present, VectorPriorSha256,
                VectorProviderUniqueName, ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
                ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1)
            .Split('\n')
            .Skip(1)
            .Select(l => l[..l.IndexOf('=', StringComparison.Ordinal)])
            .ToArray();

        Assert.Equal(ServiceOwnershipLedgerContract.BindingFieldOrder.ToArray(), emitted);
    }

    [Fact]
    public void The_binding_changes_when_the_rights_profile_changes()
    {
        string approved = ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            VectorGeneration, VectorOwnerSid, VectorServiceSid, VectorThumbprint,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            VectorKeyIdentity, ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            VectorMask, ServiceOwnershipPriorDaclState.Present, VectorPriorSha256,
            VectorProviderUniqueName, ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
            ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1);

        string unspecified = ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            VectorGeneration, VectorOwnerSid, VectorServiceSid, VectorThumbprint,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipRightsProfileId.Unspecified,
            VectorKeyIdentity, ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            VectorMask, ServiceOwnershipPriorDaclState.Present, VectorPriorSha256,
            VectorProviderUniqueName, ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
            ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1);

        Assert.NotEqual(approved, unspecified);

        // NEGATIVE CONTROL: with the profile held constant the digest is stable, so
        // the inequality above is caused by the profile and by nothing else.
        string again = ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            VectorGeneration, VectorOwnerSid, VectorServiceSid, VectorThumbprint,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            VectorKeyIdentity, ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            VectorMask, ServiceOwnershipPriorDaclState.Present, VectorPriorSha256,
            VectorProviderUniqueName, ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
            ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1);
        Assert.Equal(approved, again);

        // And the whole entry is bound to it: a document whose binding was computed
        // with the WRONG profile value no longer matches.
        DocFixture doc = PopulatedDocument();
        doc.Entry.BindingOverride = ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            1, OwnerSid, SvcSid, Thumb,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipRightsProfileId.Unspecified,
            KeyId, ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            ApprovedMaskText, ServiceOwnershipPriorDaclState.Present, PriorSha,
            ProviderUniqueNameValue, ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
            ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1);

        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.CapturedStateBindingMismatch);
    }

    // ===========================================================================
    // ENTRY SHAPE AND PROJECTION
    // ===========================================================================

    [Fact]
    public void The_entry_property_set_carries_the_profile_as_a_required_closed_property()
    {
        string[] names = ServiceOwnershipLedgerContract.EntryPropertyNames.ToArray();

        Assert.Equal(
            new[]
            {
                "entryId", "owningUserSid", "serviceSid", "credentialKind",
                "certificateThumbprintSha1", "provenance", "privateKeyProviderKind",
                "rightsProfileId", "keyIdentity", "grantMechanism", "grantedRightsMask",
                "rightsPolicyVersion", "priorDaclState", "priorDaclBytesBase64",
                "priorDaclSha256", "capturedStateBindingSha256", "associatedPromotedJobIds",
                "lifecycleState", "createdUtc", "updatedUtc",
                "providerUniqueName", "keyStorageRoot", "descriptorFormat",
            },
            names);

        Assert.Equal(23, names.Length);
        Assert.Equal(7, Array.IndexOf(names, "rightsProfileId"));
    }

    [Fact]
    public void The_accepted_entry_projects_the_profile_and_the_exact_provider()
    {
        ServiceOwnershipLedgerValidationResult result =
            ServiceOwnershipLedgerValidator.Validate(Build(PopulatedDocument()));

        Assert.True(result.IsAccepted);
        ServiceOwnershipLedgerEntry entry = Assert.Single(result.Document!.Entries);

        Assert.Equal(
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            entry.PrivateKeyProviderKind);
        Assert.Equal(ServiceOwnershipLedgerContract.ApprovedRightsProfileId, entry.RightsProfileId);
        Assert.Equal(ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl, entry.GrantMechanism);
        Assert.Equal(3, entry.RightsPolicyVersion);
        Assert.Equal(0x00120009u, entry.GrantedRightsMask.Value);
        Assert.Equal(ServiceOwnershipLifecycleState.Intended, entry.LifecycleState);
    }

    [Fact]
    public void A_broad_cng_entry_can_never_carry_the_profile_in_any_lifecycle_state()
    {
        foreach (string lifecycle in new[] { "intended", "active", "restoring", "restored", "stale" })
        {
            DocFixture doc = PopulatedDocument();
            doc.TransactionState = "credential-mutated";
            doc.Entry.Provider = "cng";
            doc.Entry.Lifecycle = lifecycle;

            ServiceOwnershipLedgerValidationResult result =
                ServiceOwnershipLedgerValidator.Validate(Build(doc));

            Assert.True(result.IsRefused, lifecycle);
            Assert.Equal(ServiceOwnershipLedgerInvalidReason.ProviderMechanismMismatch, result.Reason);
            Assert.Equal(ServiceOwnershipLedgerOutcome.Inconsistent, result.Outcome);
        }
    }

    [Fact]
    public void A_legacy_csp_entry_can_never_carry_the_profile_in_any_lifecycle_state()
    {
        foreach (string lifecycle in new[] { "intended", "active", "restoring", "restored", "stale" })
        {
            DocFixture doc = PopulatedDocument();
            doc.TransactionState = "credential-mutated";
            doc.Entry.Provider = "legacy-csp";
            doc.Entry.Mechanism = CspMechanismToken;
            doc.Entry.Lifecycle = lifecycle;

            ServiceOwnershipLedgerValidationResult result =
                ServiceOwnershipLedgerValidator.Validate(Build(doc));

            Assert.True(result.IsRefused, lifecycle);
            Assert.Equal(
                ServiceOwnershipLedgerInvalidReason.UnsupportedProviderRightsVocabulary,
                result.Reason);
            Assert.Equal(ServiceOwnershipLedgerOutcome.Unsupported, result.Outcome);
        }
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("-1")]
    public void A_ledger_entry_declaring_the_wrong_rights_policy_version_is_refused(string raw)
    {
        DocFixture doc = PopulatedDocument();
        doc.Entry.PolicyVersionRaw = raw;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidRightsPolicyVersion);
    }

    // ===========================================================================
    // STRUCTURAL CONTAINMENT
    // ===========================================================================

    [Fact]
    public void Cross_entry_equality_compares_the_rights_profile()
    {
        // Only ONE profile value can appear on the wire, so two entries can never
        // legally DISAGREE about it today. The obligation is therefore proven
        // structurally: the shared-certificate comparison must name the field.
        string code = StripComments(File.ReadAllText(ContractPath()));

        Assert.Contains(
            "first.RightsProfileId == other.RightsProfileId",
            code,
            StringComparison.Ordinal);

        // POSITIVE CONTROL: the same reader really does drop commented-out text, so
        // a comment could not have satisfied the assertion above.
        Assert.DoesNotContain(
            "SENTINEL_THAT_MUST_NOT_SURVIVE",
            StripComments("// SENTINEL_THAT_MUST_NOT_SURVIVE"),
            StringComparison.Ordinal);
        Assert.Contains(
            "SENTINEL_THAT_MUST_NOT_SURVIVE",
            StripComments("var x = SENTINEL_THAT_MUST_NOT_SURVIVE;"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_contract_declares_no_grant_apply_persist_or_execution_surface()
    {
        string code = StripComments(File.ReadAllText(ContractPath()));

        foreach (string token in new[]
                 {
                     "ApplyGrant", "GrantAccess", "SetAccessControl", "AddAccessRule",
                     "CryptoKeySecurity", "CryptoKeyRights", "X509Store", "CngKey",
                     "RegistryKey", "ServiceController", "Process", "File.", "Directory.",
                     "StartCook", "StartBake", "PAX_Purview",
                 })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }

        // POSITIVE CONTROL: the scanner can see a real occurrence.
        Assert.Contains(
            "SetAccessControl",
            StripComments("key.SetAccessControl(security);"),
            StringComparison.Ordinal);
    }

    // CYCLE 85 CAPABILITY-BOUNDARY NARROWING (supersedes the flat cycle-51 rule).
    //
    // THE RULE IS NOW TOKEN-SPECIFIC AND EXACT-PATH. There are exactly THREE
    // authorized ledger tokens, and each is bound to EXACTLY ONE full file path.
    // A file's allowance covers ONLY ITS OWN token, so the writer token inside the
    // executor file is an offender, the executor token inside the writer file is an
    // offender, and either of them inside the reader file is an offender.
    //
    // TRUTHFUL STATUS OF THE THREE AUTHORIZED FILES:
    //   * the READER file EXISTS today;
    //   * the WRITER file is a FUTURE file that DOES NOT EXIST yet;
    //   * the EXECUTOR file is a FUTURE file that DOES NOT EXIST yet.
    // No writer or executor implementation is claimed or required by this guard.
    // What IS required today is ZERO writer-token declarations under src/, ZERO
    // executor-token declarations under src/, and EXACTLY ONE reader-declaring
    // file which must be the authorized reader path. Existence checks for the
    // future files belong to the cycle that actually lands them.
    //
    // TRUTHFUL NOTE ON THE EXECUTOR ALLOWANCE: it is DEFENSIVE, not unblocking.
    // "ServiceOwnershipPromotionExecutor" does NOT contain the token
    // "OwnershipLedgerExecutor" (Promotion, not Ledger), so that class name would
    // never have tripped the guard in the first place. The WRITER allowance IS
    // load-bearing, because "ServiceOwnershipLedgerWriter" DOES contain
    // "OwnershipLedgerWriter".
    //
    // OBSERVER, STORE and REPOSITORY remain GLOBALLY FORBIDDEN with NO allowance
    // anywhere - including inside all three authorized files, and including
    // inside the cycle-94 composition root.
    //
    // The file comparison is FULL CANONICAL PATH equality, OrdinalIgnoreCase.
    // There is NO directory-wide exception, NO filename pattern, NO suffix/prefix/
    // wildcard/substring match, NO shared allowance and NO class-name matching.
    //
    // ===========================================================================
    // CYCLE 94 AMENDMENT (G7), UNDER EXPLICIT AUTHORITY. THE EXACT CARDINALITIES.
    // ===========================================================================
    //
    // Pass C introduces exactly ONE composition root -
    // src/PAXCookbookSetup/Service/ServiceOwnershipElevatedTransaction.cs - which
    // calls the ledger reader and constructs the promotion executor. The
    // allowance table therefore becomes:
    //
    //   ReaderToken    -> EXACTLY TWO paths: the reader file + the composition root
    //   WriterToken    -> EXACTLY ONE path : the writer file, unchanged
    //   ExecutorToken  -> EXACTLY TWO paths: the executor file + the composition root
    //
    // THE SHARED PATH IS DELIBERATE AND IS THE ONLY ONE. Reader and Executor
    // intentionally share the composition-root path, because one file legitimately
    // does both jobs. That is asserted as an EXACT fact below, not tolerated: any
    // OTHER duplicate allowed path fails, and the shared path must be exactly the
    // composition root.
    //
    // THE WRITER ALLOWANCE IS UNCHANGED AND STAYS AT ONE. The composition root
    // must never name the writer token; it constructs
    // ServiceOwnershipFixedLedgerPersistencePort, which does not carry the token.
    // That is asserted directly.
    //
    // NOTHING ELSE WIDENED. Exact full-path CASE-INSENSITIVE comparison remains
    // the ONLY matcher, the globally forbidden tokens keep NO allowance anywhere,
    // and every adversarial and mutation control is retained - plus new ones for
    // a third path, a wildcard, a leaf-name match, a directory exception, a
    // relocated root and a facade.
    private const string ReaderToken = "OwnershipLedgerReader";
    private const string WriterToken = "OwnershipLedgerWriter";
    private const string ExecutorToken = "OwnershipLedgerExecutor";

    // NO allowance exists for these anywhere under src/, in any file.
    private static readonly string[] GloballyForbiddenLedgerTokens =
    {
        "OwnershipLedgerObserver", "ServiceOwnershipLedgerStore", "ServiceOwnershipLedgerRepository",
    };

    /// <summary>
    /// The ONE composition root cycle 94 authorized, as an INDEPENDENT
    /// forward-slash literal so a relocation of the segment arrays alone
    /// contradicts a literal that did not move with them.
    /// </summary>
    private const string IndependentCompositionRootRelativePath =
        "src/PAXCookbookSetup/Service/ServiceOwnershipElevatedTransaction.cs";

    // Token -> the EXACT files that may contain it. One entry per token; the
    // cardinality of each entry is asserted directly below.
    private static readonly (string Token, string[][] RelativeSegmentSets)[] AuthorizedLedgerTokenFiles =
    {
        (ReaderToken, new[]
        {
            new[] { "src", "PAXCookbookSetup", "Service", "ServiceOwnershipLedgerReader.cs" },
            new[] { "src", "PAXCookbookSetup", "Service", "ServiceOwnershipElevatedTransaction.cs" },
        }),
        (WriterToken, new[]
        {
            new[] { "src", "PAXCookbookSetup", "Service", "ServiceOwnershipLedgerWriter.cs" },
        }),
        (ExecutorToken, new[]
        {
            new[] { "src", "PAXCookbookSetup", "Service", "ServiceOwnershipPromotionExecutor.cs" },
            new[] { "src", "PAXCookbookSetup", "Service", "ServiceOwnershipElevatedTransaction.cs" },
        }),
    };

    /// <summary>The exact number of authorized paths each token may carry.</summary>
    private static readonly (string Token, int Count)[] RequiredAllowanceCardinalities =
    {
        (ReaderToken, 2),
        (WriterToken, 1),
        (ExecutorToken, 2),
    };

    // The ONLY comparison the production rule uses: whole canonical path equality.
    private static readonly Func<string, string, bool> ExactFullPathMatch =
        static (actualFullPath, allowedFullPath) =>
            string.Equals(actualFullPath, allowedFullPath, StringComparison.OrdinalIgnoreCase);

    private static string RepoFullPath(params string[] relativeSegments)
    {
        string path = RepoRoot();
        foreach (string segment in relativeSegments)
        {
            path = Path.Combine(path, segment);
        }
        return Path.GetFullPath(path);
    }

    private static string CompositionRootFullPath() =>
        IndependentlyPinnedFullPath(IndependentCompositionRootRelativePath);

    private static IReadOnlyList<string> AuthorizedFilesFor(string token)
    {
        foreach ((string candidate, string[][] segmentSets) in AuthorizedLedgerTokenFiles)
        {
            if (string.Equals(candidate, token, StringComparison.Ordinal))
            {
                var paths = new List<string>();
                foreach (string[] segments in segmentSets)
                {
                    paths.Add(RepoFullPath(segments));
                }
                return paths;
            }
        }

        throw new InvalidOperationException("no authorized file is declared for token " + token);
    }

    /// <summary>
    /// The token's OWN declaring file - the first authorized path. Every
    /// pre-existing control uses this, so the widening cannot change what those
    /// controls mean.
    /// </summary>
    private static string AuthorizedFileFor(string token) => AuthorizedFilesFor(token)[0];

    private static string AllowedReaderFullPath() => AuthorizedFileFor(ReaderToken);

    private static List<(string Token, IReadOnlyList<string> AllowedFullPaths)> ProductionAllowances()
    {
        var allowances = new List<(string Token, IReadOnlyList<string> AllowedFullPaths)>();
        foreach ((string token, string[][] _) in AuthorizedLedgerTokenFiles)
        {
            allowances.Add((token, AuthorizedFilesFor(token)));
        }
        return allowances;
    }

    // Shared scan logic used by the real src/ sweep, by every synthetic
    // adversarial control, AND by the mutation controls, so all three exercise the
    // EXACT SAME rule rather than a paraphrase of it. The rule INPUTS (allowance
    // table, globally forbidden list, file matcher) are parameters purely so the
    // mutation controls can weaken them without touching the production rule.
    private static List<string> ScanForLedgerGuardOffenders(
        string fileFullPath,
        string code,
        IReadOnlyList<(string Token, IReadOnlyList<string> AllowedFullPaths)> allowances,
        IReadOnlyList<string> globallyForbiddenTokens,
        Func<string, string, bool> fileMatches)
    {
        var offenders = new List<string>();
        string canonicalFile = Path.GetFullPath(fileFullPath);

        foreach (string token in globallyForbiddenTokens)
        {
            if (code.Contains(token, StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(fileFullPath) + ":" + token + " (globally forbidden, no allowance anywhere)");
            }
        }

        foreach ((string token, IReadOnlyList<string> allowedFullPaths) in allowances)
        {
            if (!code.Contains(token, StringComparison.Ordinal))
            {
                continue;
            }

            bool inItsOwnAuthorizedFile = false;
            foreach (string allowedFullPath in allowedFullPaths)
            {
                if (fileMatches(canonicalFile, allowedFullPath))
                {
                    inItsOwnAuthorizedFile = true;
                    break;
                }
            }

            if (!inItsOwnAuthorizedFile)
            {
                offenders.Add(Path.GetFileName(fileFullPath) + ":" + token + " (outside its own single authorized file)");
            }
        }

        return offenders;
    }

    // The PRODUCTION rule. Everything the real sweep and the adversarial controls
    // call goes through here, with the production allowance table, the production
    // globally-forbidden list, and exact full-path equality.
    private static List<string> ScanForLedgerGuardOffenders(string fileFullPath, string code) =>
        ScanForLedgerGuardOffenders(
            fileFullPath, code, ProductionAllowances(), GloballyForbiddenLedgerTokens, ExactFullPathMatch);

    [Fact]
    public void The_production_allowance_table_binds_exactly_three_tokens_to_their_exact_authorized_files()
    {
        List<(string Token, IReadOnlyList<string> AllowedFullPaths)> allowances = ProductionAllowances();

        Assert.Equal(3, allowances.Count);

        var seenTokens = new List<string>();
        var allPaths = new List<string>();
        foreach ((string token, IReadOnlyList<string> allowedFullPaths) in allowances)
        {
            // THE EXACT CARDINALITY PER TOKEN. A silently added path fails here
            // before any sweep runs.
            int required = RequiredCardinalityFor(token);
            Assert.Equal(required, allowedFullPaths.Count);

            Assert.DoesNotContain(token, seenTokens, StringComparer.Ordinal);
            foreach (string path in allowedFullPaths)
            {
                Assert.Equal(path, Path.GetFullPath(path), StringComparer.Ordinal);
                allPaths.Add(path);
            }
            seenTokens.Add(token);
        }

        Assert.Equal(new[] { ReaderToken, WriterToken, ExecutorToken }, seenTokens);
        Assert.Equal(new[] { 2, 1, 2 }, allowances.Select(a => a.AllowedFullPaths.Count).ToArray());
        Assert.Equal(5, allPaths.Count);

        // THE ONE PERMITTED DUPLICATE, ASSERTED EXACTLY. The composition root is
        // shared by Reader and Executor and by nothing else; every other allowed
        // path is unique across the whole table.
        var duplicates = allPaths
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        string onlyDuplicate = Assert.Single(duplicates);
        Assert.Equal(CompositionRootFullPath(), onlyDuplicate, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            2,
            allPaths.Count(p => string.Equals(p, CompositionRootFullPath(), StringComparison.OrdinalIgnoreCase)));

        // ...and it is shared by EXACTLY Reader and Executor - never the writer.
        Assert.Contains(CompositionRootFullPath(), AuthorizedFilesFor(ReaderToken), StringComparer.OrdinalIgnoreCase);
        Assert.Contains(CompositionRootFullPath(), AuthorizedFilesFor(ExecutorToken), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(CompositionRootFullPath(), AuthorizedFilesFor(WriterToken), StringComparer.OrdinalIgnoreCase);

        // Each token's OWN declaring file is still its first authorized path.
        Assert.Equal(AuthorizedFileFor(ReaderToken), allowances[0].AllowedFullPaths[0], StringComparer.Ordinal);
        Assert.Equal(AuthorizedFileFor(WriterToken), allowances[1].AllowedFullPaths[0], StringComparer.Ordinal);
        Assert.Equal(AuthorizedFileFor(ExecutorToken), allowances[2].AllowedFullPaths[0], StringComparer.Ordinal);

        // No authorized token may also sit on the globally forbidden list, and no
        // globally forbidden token may acquire an allowance.
        foreach (string forbidden in GloballyForbiddenLedgerTokens)
        {
            Assert.DoesNotContain(forbidden, seenTokens, StringComparer.Ordinal);
        }
        Assert.Equal(3, GloballyForbiddenLedgerTokens.Length);
    }

    private static int RequiredCardinalityFor(string token)
    {
        foreach ((string candidate, int count) in RequiredAllowanceCardinalities)
        {
            if (string.Equals(candidate, token, StringComparison.Ordinal))
            {
                return count;
            }
        }

        throw new InvalidOperationException("no required cardinality is declared for token " + token);
    }

    [Fact]
    public void No_production_source_declares_a_globally_forbidden_ledger_token_and_each_authorized_token_appears_only_in_its_own_exact_authorized_file()
    {
        string[] sources = Directory
            .GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(sources);

        // CYCLE 88 - EXACT PRESENCE REPLACES TEMPORAL ABSENCE. All three authorized
        // files exist now, so the guard REQUIRES each of them at its exact pinned
        // location rather than merely tolerating its future arrival.
        Assert.True(File.Exists(AllowedReaderFullPath()), "the single authorized reader file must exist: " + AllowedReaderFullPath());
        Assert.True(File.Exists(AuthorizedFileFor(WriterToken)), "the single authorized writer file must exist: " + AuthorizedFileFor(WriterToken));
        Assert.True(File.Exists(AuthorizedFileFor(ExecutorToken)), "the single authorized executor file must exist: " + AuthorizedFileFor(ExecutorToken));

        // CYCLE 94 - THE ONE COMPOSITION ROOT MUST EXIST AT ITS EXACT PATH TOO.
        // The widened allowance is worthless if the file it points at is absent
        // or has moved, so this is an EXACT presence assertion, not a tolerance.
        Assert.True(
            File.Exists(CompositionRootFullPath()),
            "the one authorized composition root must exist: " + CompositionRootFullPath());

        // ...and each allowance-derived path must equal this file's OWN independent
        // literal, so relocating the allowance table alone cannot pass unnoticed.
        Assert.Equal(IndependentlyPinnedFullPath(IndependentWriterRelativePath), AuthorizedFileFor(WriterToken), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(IndependentlyPinnedFullPath(IndependentExecutorRelativePath), AuthorizedFileFor(ExecutorToken), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            IndependentlyPinnedFullPath(IndependentCompositionRootRelativePath),
            AuthorizedFilesFor(ReaderToken)[1],
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            IndependentlyPinnedFullPath(IndependentCompositionRootRelativePath),
            AuthorizedFilesFor(ExecutorToken)[1],
            StringComparer.OrdinalIgnoreCase);

        var offenders = new List<string>();
        var readerDeclaringFiles = new List<string>();
        var writerDeclaringFiles = new List<string>();
        var executorDeclaringFiles = new List<string>();
        int globallyForbiddenHits = 0;
        int controlHits = 0;

        foreach (string file in sources)
        {
            string code = StripComments(File.ReadAllText(file));

            offenders.AddRange(ScanForLedgerGuardOffenders(file, code));

            if (code.Contains(ReaderToken, StringComparison.Ordinal))
            {
                readerDeclaringFiles.Add(Path.GetFullPath(file));
            }
            if (code.Contains(WriterToken, StringComparison.Ordinal))
            {
                writerDeclaringFiles.Add(Path.GetFullPath(file));
            }
            if (code.Contains(ExecutorToken, StringComparison.Ordinal))
            {
                executorDeclaringFiles.Add(Path.GetFullPath(file));
            }
            foreach (string forbidden in GloballyForbiddenLedgerTokens)
            {
                if (code.Contains(forbidden, StringComparison.Ordinal))
                {
                    globallyForbiddenHits++;
                }
            }

            // POSITIVE CONTROL for the same scan over the same file set: a symbol
            // that genuinely exists must be found, or the zeroes above are vacuous.
            if (code.Contains("ServiceOwnershipLedgerContract", StringComparison.Ordinal))
            {
                controlHits++;
            }
        }

        Assert.Empty(offenders);
        Assert.True(controlHits > 0, "the ledger token scan never found a symbol that does exist");

        // CYCLE 88 - THE WRITER ALLOWANCE IS NOW SWITCHED ON, AND ONLY BARELY.
        // Brian authorized ONE writer at ONE location, so absence becomes EXACT
        // PRESENCE: exactly one file under src/ may declare the writer token, and it
        // must be the pinned writer path. A second declaring file, or the same
        // declaration at any other path, still fails.
        string onlyWriterFile = Assert.Single(writerDeclaringFiles);
        Assert.Equal(AuthorizedFileFor(WriterToken), onlyWriterFile, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(IndependentlyPinnedFullPath(IndependentWriterRelativePath), onlyWriterFile, StringComparer.OrdinalIgnoreCase);

        // RULING 4 - THE EXECUTOR TOKEN STAYS AT ZERO, DELIBERATELY.
        // "ServiceOwnershipPromotionExecutor" does NOT contain
        // "OwnershipLedgerExecutor" (Promotion, not Ledger), so the executor's
        // existence is proven by the exact FILE assertion above rather than by a
        // token count. Inserting the token merely to move this counter would be a
        // meaningless marker, so the count stays zero and the allowance stays
        // defensive.
        Assert.Empty(executorDeclaringFiles);
        Assert.Equal(0, globallyForbiddenHits);

        // CYCLE 94 - THE READER ALLOWANCE IS NOW EXACTLY TWO, AND ONLY BARELY.
        // EXACTLY two files under src/ may declare the reader token, and they must
        // be exactly the reader file and the one composition root. A third
        // declaring file, or either declaration at any other path, still fails.
        Assert.Equal(2, readerDeclaringFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(
            AuthorizedFilesFor(ReaderToken).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            readerDeclaringFiles.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);
        Assert.Contains(AllowedReaderFullPath(), readerDeclaringFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(CompositionRootFullPath(), readerDeclaringFiles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_composition_root_never_declares_the_writer_token_so_the_writer_allowance_stays_at_one()
    {
        // G6, ASSERTED DIRECTLY RATHER THAN INFERRED. The composition root is
        // permitted the reader and executor tokens and nothing else; the writer
        // token would make the writer allowance a lie.
        string code = StripComments(File.ReadAllText(CompositionRootFullPath()));

        Assert.DoesNotContain(WriterToken, code, StringComparison.Ordinal);

        // ...and the raw bytes carry it nowhere either, not even in a comment.
        Assert.DoesNotContain(WriterToken, File.ReadAllText(CompositionRootFullPath()), StringComparison.Ordinal);

        // POSITIVE CONTROL: the same scan DOES find the token when it is present.
        Assert.Contains(
            WriterToken,
            StripComments("class X { ServiceOwnershipLedgerWriter w; }"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReaderToken)]
    [InlineData(WriterToken)]
    [InlineData(ExecutorToken)]
    public void Each_authorized_token_is_permitted_inside_its_own_exact_authorized_file(string token)
    {
        // Positive direction: the reader token in the reader file, the writer token
        // in the writer file, and the executor token in the executor file all pass.
        // The writer and executor cases pass SYNTHETICALLY - neither file exists.
        string code = "class X { void M() { var y = " + token + ".Do(); } }";

        List<string> offenders = ScanForLedgerGuardOffenders(AuthorizedFileFor(token), code);

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData(ReaderToken, WriterToken)]
    [InlineData(ReaderToken, ExecutorToken)]
    [InlineData(WriterToken, ReaderToken)]
    [InlineData(WriterToken, ExecutorToken)]
    [InlineData(ExecutorToken, ReaderToken)]
    [InlineData(ExecutorToken, WriterToken)]
    public void An_authorized_token_is_still_caught_inside_another_tokens_authorized_file(string token, string otherTokenWhoseFileIsUsed)
    {
        // The allowance is per-token, NOT per-file and NOT shared: the writer token
        // in the executor file fails, the executor token in the writer file fails,
        // and either one in the reader file fails.
        string code = "class X { void M() { var y = " + token + ".Do(); } }";

        List<string> offenders = ScanForLedgerGuardOffenders(AuthorizedFileFor(otherTokenWhoseFileIsUsed), code);

        string offender = Assert.Single(offenders);
        Assert.Contains(token, offender, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReaderToken)]
    [InlineData(WriterToken)]
    [InlineData(ExecutorToken)]
    public void An_authorized_token_is_still_caught_in_an_unrelated_file(string token)
    {
        string syntheticOtherFile = RepoFullPath("src", "SomeOtherPlace", "NotTheLedgerFile.cs");
        string code = "class X { void M() { var y = " + token + ".Do(); } }";

        List<string> offenders = ScanForLedgerGuardOffenders(syntheticOtherFile, code);

        string offender = Assert.Single(offenders);
        Assert.Contains(token, offender, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReaderToken, "ServiceOwnershipLedgerReader2.cs")]
    [InlineData(ReaderToken, "ServiceOwnershipLedgerReader.cs.cs")]
    [InlineData(ReaderToken, "_ServiceOwnershipLedgerReader.cs")]
    [InlineData(WriterToken, "ServiceOwnershipLedgerWriter2.cs")]
    [InlineData(WriterToken, "ServiceOwnershipLedgerWriter.cs.cs")]
    [InlineData(WriterToken, "_ServiceOwnershipLedgerWriter.cs")]
    [InlineData(ExecutorToken, "ServiceOwnershipPromotionExecutor2.cs")]
    [InlineData(ExecutorToken, "ServiceOwnershipPromotionExecutor.cs.cs")]
    [InlineData(ExecutorToken, "_ServiceOwnershipPromotionExecutor.cs")]
    public void An_authorized_token_is_still_caught_in_a_near_miss_filename_in_the_correct_directory(string token, string nearMissFileName)
    {
        string nearMissFile = RepoFullPath("src", "PAXCookbookSetup", "Service", nearMissFileName);
        Assert.NotEqual(AuthorizedFileFor(token), nearMissFile, StringComparer.OrdinalIgnoreCase);

        string code = "class X { void M() { var y = " + token + ".Do(); } }";

        List<string> offenders = ScanForLedgerGuardOffenders(nearMissFile, code);

        string offender = Assert.Single(offenders);
        Assert.Contains(token, offender, StringComparison.Ordinal);
    }

    [Fact]
    public void All_three_authorized_tokens_are_caught_in_the_correct_directory_but_a_wholly_different_filename()
    {
        // The directory is NOT the unit of allowance. A sibling file in the exact
        // authorized directory catches all three tokens at once.
        string siblingInSameDirectory = RepoFullPath("src", "PAXCookbookSetup", "Service", "ServiceOwnershipHelper.cs");
        string code =
            "class X { void M() { var a = " + ReaderToken + ".Do(); var b = " + WriterToken +
            ".Do(); var c = " + ExecutorToken + ".Do(); } }";

        List<string> offenders = ScanForLedgerGuardOffenders(siblingInSameDirectory, code);

        Assert.Equal(3, offenders.Count);
        Assert.Contains(offenders, o => o.Contains(ReaderToken, StringComparison.Ordinal));
        Assert.Contains(offenders, o => o.Contains(WriterToken, StringComparison.Ordinal));
        Assert.Contains(offenders, o => o.Contains(ExecutorToken, StringComparison.Ordinal));
    }

    [Fact]
    public void All_three_authorized_tokens_are_caught_in_a_near_miss_filename()
    {
        string nearMissFile = RepoFullPath("src", "PAXCookbookSetup", "Service", "ServiceOwnershipLedgerWriterHelper.cs");
        string code =
            "class X { void M() { var a = " + ReaderToken + ".Do(); var b = " + WriterToken +
            ".Do(); var c = " + ExecutorToken + ".Do(); } }";

        List<string> offenders = ScanForLedgerGuardOffenders(nearMissFile, code);

        Assert.Equal(3, offenders.Count);
    }

    [Theory]
    [InlineData(ReaderToken, "OwnershipLedgerObserver")]
    [InlineData(ReaderToken, "ServiceOwnershipLedgerStore")]
    [InlineData(ReaderToken, "ServiceOwnershipLedgerRepository")]
    [InlineData(WriterToken, "OwnershipLedgerObserver")]
    [InlineData(WriterToken, "ServiceOwnershipLedgerStore")]
    [InlineData(WriterToken, "ServiceOwnershipLedgerRepository")]
    [InlineData(ExecutorToken, "OwnershipLedgerObserver")]
    [InlineData(ExecutorToken, "ServiceOwnershipLedgerStore")]
    [InlineData(ExecutorToken, "ServiceOwnershipLedgerRepository")]
    public void Globally_forbidden_tokens_are_caught_inside_every_authorized_file(string authorizedTokenWhoseFileIsUsed, string forbiddenToken)
    {
        // No file allowance ever covers observer, store or repository.
        string code = "class X { void M() { var y = " + forbiddenToken + ".Do(); } }";

        List<string> offenders = ScanForLedgerGuardOffenders(AuthorizedFileFor(authorizedTokenWhoseFileIsUsed), code);

        string offender = Assert.Single(offenders);
        Assert.Contains(forbiddenToken, offender, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReaderToken)]
    [InlineData(WriterToken)]
    [InlineData(ExecutorToken)]
    public void Path_casing_and_separator_differences_still_identify_the_same_authorized_file(string token)
    {
        string code = "class X { void M() { var y = " + token + ".Do(); } }";
        string authorized = AuthorizedFileFor(token);

        Assert.Empty(ScanForLedgerGuardOffenders(authorized.ToUpperInvariant(), code));
        Assert.Empty(ScanForLedgerGuardOffenders(authorized.ToLowerInvariant(), code));
        Assert.Empty(ScanForLedgerGuardOffenders(
            authorized.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), code));
    }

    [Theory]
    [InlineData(ReaderToken)]
    [InlineData(WriterToken)]
    [InlineData(ExecutorToken)]
    public void A_file_outside_src_cannot_satisfy_the_production_allowance(string token)
    {
        // Same trailing path, different root: the allowance is anchored at the
        // repo-rooted src/ path, so it cannot be satisfied from anywhere else.
        string outsideSrc = RepoFullPath(
            "tests", "PAXCookbookSetup", "Service", Path.GetFileName(AuthorizedFileFor(token)));
        string code = "class X { void M() { var y = " + token + ".Do(); } }";

        List<string> offenders = ScanForLedgerGuardOffenders(outsideSrc, code);

        string offender = Assert.Single(offenders);
        Assert.Contains(token, offender, StringComparison.Ordinal);
    }

    // ===========================================================================
    // MUTATION CONTROLS
    //
    // Each control weakens ONE rule INPUT (the allowance table, the globally
    // forbidden list, or the file matcher) and feeds it to the SAME
    // ScanForLedgerGuardOffenders body the production rule uses. Each asserts BOTH
    // directions: the production rule CATCHES the probe, and the mutated rule
    // MISSES it. A mutation that changed nothing would fail its own assertion.
    //
    // RESIDUAL GAP, STATED PLAINLY: these mutate the rule's INPUTS, not the
    // scanner's statements. They prove the allowance data, the forbidden list and
    // the strictness of the path comparison are all load-bearing; they do not by
    // themselves prove that no future edit to the scanner body could weaken it.
    // The adversarial controls above cover the body in both directions instead.
    // ===========================================================================

    private static List<(string Token, IReadOnlyList<string> AllowedFullPaths)> AllowancesWithExtraPath(string token, string extraFileName)
    {
        List<(string Token, IReadOnlyList<string> AllowedFullPaths)> mutated = ProductionAllowances();
        for (int i = 0; i < mutated.Count; i++)
        {
            if (string.Equals(mutated[i].Token, token, StringComparison.Ordinal))
            {
                // APPEND, never replace. Replacing would silently NARROW the
                // cycle-94 allowance and make the control test a different rule.
                var widened = new List<string>(mutated[i].AllowedFullPaths)
                {
                    RepoFullPath("src", "PAXCookbookSetup", "Service", extraFileName),
                };
                mutated[i] = (token, widened);
            }
        }
        return mutated;
    }

    [Fact]
    public void MUTATION_widening_the_writer_allowance_to_the_whole_service_directory_is_caught()
    {
        string probeFile = RepoFullPath("src", "PAXCookbookSetup", "Service", "SomeUnrelatedServiceFile.cs");
        string code = "class X { void M() { var y = " + WriterToken + ".Do(); } }";

        Assert.NotEmpty(ScanForLedgerGuardOffenders(probeFile, code));

        Func<string, string, bool> directoryWideMatch = static (actual, allowed) =>
            string.Equals(Path.GetDirectoryName(actual), Path.GetDirectoryName(allowed), StringComparison.OrdinalIgnoreCase);

        Assert.Empty(ScanForLedgerGuardOffenders(
            probeFile, code, ProductionAllowances(), GloballyForbiddenLedgerTokens, directoryWideMatch));
    }

    [Fact]
    public void MUTATION_using_a_suffix_match_for_the_executor_allowance_is_caught()
    {
        string probeFile = RepoFullPath("src", "Elsewhere", "ServiceOwnershipPromotionExecutor.cs");
        string code = "class X { void M() { var y = " + ExecutorToken + ".Do(); } }";

        Assert.NotEmpty(ScanForLedgerGuardOffenders(probeFile, code));

        Func<string, string, bool> suffixMatch = static (actual, allowed) =>
            actual.EndsWith(Path.GetFileName(allowed), StringComparison.OrdinalIgnoreCase);

        Assert.Empty(ScanForLedgerGuardOffenders(
            probeFile, code, ProductionAllowances(), GloballyForbiddenLedgerTokens, suffixMatch));
    }

    [Fact]
    public void MUTATION_permitting_a_second_writer_filename_is_caught()
    {
        string probeFile = RepoFullPath("src", "PAXCookbookSetup", "Service", "ServiceOwnershipLedgerWriter2.cs");
        string code = "class X { void M() { var y = " + WriterToken + ".Do(); } }";

        Assert.NotEmpty(ScanForLedgerGuardOffenders(probeFile, code));

        Assert.Empty(ScanForLedgerGuardOffenders(
            probeFile,
            code,
            AllowancesWithExtraPath(WriterToken, "ServiceOwnershipLedgerWriter2.cs"),
            GloballyForbiddenLedgerTokens,
            ExactFullPathMatch));
    }

    [Fact]
    public void MUTATION_permitting_a_second_executor_filename_is_caught()
    {
        string probeFile = RepoFullPath("src", "PAXCookbookSetup", "Service", "ServiceOwnershipPromotionExecutor2.cs");
        string code = "class X { void M() { var y = " + ExecutorToken + ".Do(); } }";

        Assert.NotEmpty(ScanForLedgerGuardOffenders(probeFile, code));

        Assert.Empty(ScanForLedgerGuardOffenders(
            probeFile,
            code,
            AllowancesWithExtraPath(ExecutorToken, "ServiceOwnershipPromotionExecutor2.cs"),
            GloballyForbiddenLedgerTokens,
            ExactFullPathMatch));
    }

    [Theory]
    [InlineData("OwnershipLedgerObserver")]
    [InlineData("ServiceOwnershipLedgerStore")]
    [InlineData("ServiceOwnershipLedgerRepository")]
    public void MUTATION_granting_an_allowance_to_a_globally_forbidden_token_is_caught(string forbiddenToken)
    {
        string probeFile = AllowedReaderFullPath();
        string code = "class X { void M() { var y = " + forbiddenToken + ".Do(); } }";

        Assert.NotEmpty(ScanForLedgerGuardOffenders(probeFile, code));

        List<(string Token, IReadOnlyList<string> AllowedFullPaths)> mutatedAllowances = ProductionAllowances();
        mutatedAllowances.Add((forbiddenToken, new[] { probeFile }));

        var mutatedForbidden = new List<string>();
        foreach (string token in GloballyForbiddenLedgerTokens)
        {
            if (!string.Equals(token, forbiddenToken, StringComparison.Ordinal))
            {
                mutatedForbidden.Add(token);
            }
        }

        Assert.Empty(ScanForLedgerGuardOffenders(
            probeFile, code, mutatedAllowances, mutatedForbidden, ExactFullPathMatch));
    }

    [Fact]
    public void MUTATION_swapping_the_writer_and_executor_allowances_is_caught()
    {
        string writerInExecutorFile = "class X { void M() { var y = " + WriterToken + ".Do(); } }";
        string executorInWriterFile = "class X { void M() { var y = " + ExecutorToken + ".Do(); } }";

        Assert.NotEmpty(ScanForLedgerGuardOffenders(AuthorizedFileFor(ExecutorToken), writerInExecutorFile));
        Assert.NotEmpty(ScanForLedgerGuardOffenders(AuthorizedFileFor(WriterToken), executorInWriterFile));

        var swapped = new List<(string Token, IReadOnlyList<string> AllowedFullPaths)>
        {
            (ReaderToken, new[] { AuthorizedFileFor(ReaderToken) }),
            (WriterToken, new[] { AuthorizedFileFor(ExecutorToken) }),
            (ExecutorToken, new[] { AuthorizedFileFor(WriterToken) }),
        };

        Assert.Empty(ScanForLedgerGuardOffenders(
            AuthorizedFileFor(ExecutorToken), writerInExecutorFile, swapped, GloballyForbiddenLedgerTokens, ExactFullPathMatch));
        Assert.Empty(ScanForLedgerGuardOffenders(
            AuthorizedFileFor(WriterToken), executorInWriterFile, swapped, GloballyForbiddenLedgerTokens, ExactFullPathMatch));
    }

    [Fact]
    public void MUTATION_weakening_reader_containment_is_caught()
    {
        string probeFile = RepoFullPath("src", "SomeOtherPlace", "NotTheReader.cs");
        string code = "class X { void M() { var y = " + ReaderToken + ".Read(); } }";

        Assert.NotEmpty(ScanForLedgerGuardOffenders(probeFile, code));

        // Reader containment dropped entirely: the token becomes unconstrained.
        var withoutReaderContainment = new List<(string Token, IReadOnlyList<string> AllowedFullPaths)>
        {
            (WriterToken, AuthorizedFilesFor(WriterToken)),
            (ExecutorToken, AuthorizedFilesFor(ExecutorToken)),
        };

        Assert.Empty(ScanForLedgerGuardOffenders(
            probeFile, code, withoutReaderContainment, GloballyForbiddenLedgerTokens, ExactFullPathMatch));
    }

    // ===========================================================================
    // CYCLE 94 MUTATION CONTROLS FOR THE WIDENED READER / EXECUTOR ALLOWANCES
    //
    // Each one proves the SAME thing twice: the production rule CATCHES the probe,
    // and a weakening of one rule INPUT MISSES it. Together they show that the
    // composition-root allowance is exactly one extra path and nothing more.
    // ===========================================================================

    [Theory]
    [InlineData(ReaderToken)]
    [InlineData(ExecutorToken)]
    public void The_composition_root_is_permitted_the_reader_and_executor_tokens(string token)
    {
        string code = "class X { void M() { var y = " + token + ".Do(); } }";

        Assert.Empty(ScanForLedgerGuardOffenders(CompositionRootFullPath(), code));
    }

    [Fact]
    public void The_writer_token_inside_the_composition_root_is_still_an_offender()
    {
        // The composition root's allowance covers exactly TWO tokens. The writer
        // is not one of them, so G6 is enforced by the guard itself, not merely by
        // the root's current contents.
        string code = "class X { void M() { var y = " + WriterToken + ".Write(); } }";

        string offender = Assert.Single(ScanForLedgerGuardOffenders(CompositionRootFullPath(), code));
        Assert.Contains(WriterToken, offender, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OwnershipLedgerObserver")]
    [InlineData("ServiceOwnershipLedgerStore")]
    [InlineData("ServiceOwnershipLedgerRepository")]
    public void A_globally_forbidden_token_inside_the_composition_root_is_still_an_offender(string forbiddenToken)
    {
        // The widened allowance grants NOTHING to the globally forbidden set.
        string code = "class X { " + forbiddenToken + " f; }";

        string offender = Assert.Single(ScanForLedgerGuardOffenders(CompositionRootFullPath(), code));
        Assert.Contains(forbiddenToken, offender, StringComparison.Ordinal);

        // ...and the real composition root does not carry it.
        Assert.DoesNotContain(
            forbiddenToken,
            StripComments(File.ReadAllText(CompositionRootFullPath())),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReaderToken)]
    [InlineData(ExecutorToken)]
    public void MUTATION_permitting_a_third_path_for_a_widened_token_is_caught(string token)
    {
        string probeFile = RepoFullPath("src", "PAXCookbookSetup", "Service", "ServiceOwnershipElevatedTransaction2.cs");
        string code = "class X { void M() { var y = " + token + ".Do(); } }";

        Assert.NotEmpty(ScanForLedgerGuardOffenders(probeFile, code));

        List<(string Token, IReadOnlyList<string> AllowedFullPaths)> widened =
            AllowancesWithExtraPath(token, "ServiceOwnershipElevatedTransaction2.cs");

        // The mutation really did ADD a path rather than move one.
        Assert.Equal(
            RequiredCardinalityFor(token) + 1,
            widened.Single(a => string.Equals(a.Token, token, StringComparison.Ordinal)).AllowedFullPaths.Count);

        Assert.Empty(ScanForLedgerGuardOffenders(
            probeFile, code, widened, GloballyForbiddenLedgerTokens, ExactFullPathMatch));
    }

    [Theory]
    [InlineData(ReaderToken)]
    [InlineData(ExecutorToken)]
    public void MUTATION_a_relocated_composition_root_is_caught(string token)
    {
        // Same leaf name, different directory. The allowance is anchored at the
        // repo-rooted path, so a relocated root can never satisfy it.
        string relocated = RepoFullPath("src", "Elsewhere", "ServiceOwnershipElevatedTransaction.cs");
        string code = "class X { void M() { var y = " + token + ".Do(); } }";

        Assert.NotEqual(CompositionRootFullPath(), relocated, StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(ScanForLedgerGuardOffenders(relocated, code));

        Func<string, string, bool> leafNameMatch = static (actual, allowed) =>
            string.Equals(Path.GetFileName(actual), Path.GetFileName(allowed), StringComparison.OrdinalIgnoreCase);

        Assert.Empty(ScanForLedgerGuardOffenders(
            relocated, code, ProductionAllowances(), GloballyForbiddenLedgerTokens, leafNameMatch));
    }

    [Theory]
    [InlineData(ReaderToken)]
    [InlineData(ExecutorToken)]
    public void MUTATION_a_directory_exception_around_the_composition_root_is_caught(string token)
    {
        string sibling = RepoFullPath("src", "PAXCookbookSetup", "Service", "SomeUnrelatedServiceFile.cs");
        string code = "class X { void M() { var y = " + token + ".Do(); } }";

        Assert.NotEmpty(ScanForLedgerGuardOffenders(sibling, code));

        Func<string, string, bool> directoryWideMatch = static (actual, allowed) =>
            string.Equals(Path.GetDirectoryName(actual), Path.GetDirectoryName(allowed), StringComparison.OrdinalIgnoreCase);

        Assert.Empty(ScanForLedgerGuardOffenders(
            sibling, code, ProductionAllowances(), GloballyForbiddenLedgerTokens, directoryWideMatch));
    }

    [Theory]
    [InlineData(ReaderToken)]
    [InlineData(ExecutorToken)]
    public void MUTATION_a_wildcard_matcher_around_the_composition_root_is_caught(string token)
    {
        string anywhere = RepoFullPath("src", "PAXCookbook.App", "SomethingEntirelyUnrelated.cs");
        string code = "class X { void M() { var y = " + token + ".Do(); } }";

        Assert.NotEmpty(ScanForLedgerGuardOffenders(anywhere, code));

        Func<string, string, bool> wildcardMatch = static (_, _) => true;

        Assert.Empty(ScanForLedgerGuardOffenders(
            anywhere, code, ProductionAllowances(), GloballyForbiddenLedgerTokens, wildcardMatch));
    }

    [Fact]
    public void MUTATION_a_facade_that_moves_the_capability_out_of_the_composition_root_is_caught()
    {
        // THE FACADE THIS AMENDMENT REFUSES. A helper file that carries the reader
        // or executor token on the composition root's behalf is an offender, no
        // matter how thin it is or how close to the root it sits.
        foreach (string facade in new[]
                 {
                     "ServiceOwnershipElevatedTransactionFacade.cs",
                     "ServiceOwnershipElevatedTransactionHelper.cs",
                     "ServiceOwnershipElevatedTransactionFactory.cs",
                     "ServiceOwnershipElevatedTransactionPartial.cs",
                 })
        {
            string probe = RepoFullPath("src", "PAXCookbookSetup", "Service", facade);
            Assert.NotEqual(CompositionRootFullPath(), probe, StringComparer.OrdinalIgnoreCase);

            Assert.NotEmpty(ScanForLedgerGuardOffenders(
                probe, "class X { void M() { var y = " + ReaderToken + ".Read(); } }"));
            Assert.NotEmpty(ScanForLedgerGuardOffenders(
                probe, "class X { void M() { var y = " + ExecutorToken + ".Do(); } }"));
        }

        // POSITIVE CONTROL: the real composition root passes the same body, so the
        // refusals above are about the PATH, not about the code shape.
        Assert.Empty(ScanForLedgerGuardOffenders(
            CompositionRootFullPath(), "class X { void M() { var y = " + ReaderToken + ".Read(); } }"));
    }

    // ===========================================================================
    // CYCLE 88 - THE PRESENCE CONTRACT AND ITS CONTROLS
    // ===========================================================================
    //
    // These literals are INDEPENDENT of the allowance table above on purpose: they
    // are written as forward-slash relative paths rather than repo segment arrays,
    // so the external path-pin guard's segment-array extractor still sees EXACTLY
    // ONE authorization per role in this file, while a relocation of the table
    // alone now contradicts a literal that did not move with it.

    private const string IndependentWriterRelativePath =
        "src/PAXCookbookSetup/Service/ServiceOwnershipLedgerWriter.cs";

    private const string IndependentExecutorRelativePath =
        "src/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutor.cs";

    private static string IndependentlyPinnedFullPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(RepoRoot(), Path.Combine(relativePath.Split('/'))));

    /// <summary>
    /// The dodge cycle 87 identified and refused: naming the writer type something
    /// WITHOUT the guarded substring, inside the correctly pinned filename, would
    /// keep a pure token count green while making the guard's own rationale false.
    /// This closes it by requiring the pinned file to declare a TYPE whose name
    /// carries the token.
    /// </summary>
    [Fact]
    public void The_pinned_writer_file_declares_a_type_whose_name_carries_the_writer_token()
    {
        string writerFile = AuthorizedFileFor(WriterToken);
        Assert.True(File.Exists(writerFile));

        string code = StripComments(File.ReadAllText(writerFile));

        Assert.Contains("class ServiceOwnershipLedgerWriter", code, StringComparison.Ordinal);
        Assert.Contains(WriterToken, "ServiceOwnershipLedgerWriter", StringComparison.Ordinal);

        // A rename that dropped the token would leave the declaration absent, so
        // both halves of the contract are asserted rather than only the count.
        Assert.Contains(WriterToken, code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pinned_executor_file_declares_the_executor_type_at_its_exact_pinned_path()
    {
        string executorFile = AuthorizedFileFor(ExecutorToken);
        Assert.True(File.Exists(executorFile));

        string code = StripComments(File.ReadAllText(executorFile));

        Assert.Contains("class ServiceOwnershipPromotionExecutor", code, StringComparison.Ordinal);

        // RULING 4, ASSERTED IN BOTH DIRECTIONS: the authorized executor type name
        // genuinely does NOT contain the guarded token, which is exactly why the
        // executor's existence is pinned by FILE rather than by token count.
        Assert.DoesNotContain(ExecutorToken, "ServiceOwnershipPromotionExecutor", StringComparison.Ordinal);
        Assert.DoesNotContain(ExecutorToken, code, StringComparison.Ordinal);
    }

    [Fact]
    public void MUTATION_a_second_writer_declaring_file_is_caught()
    {
        string writerCode = "class ServiceOwnershipLedgerWriter { }";
        string second = RepoFullPath("src", "PAXCookbookSetup", "Service", "ServiceOwnershipLedgerWriter2.cs");

        // The real file passes...
        Assert.Empty(ScanForLedgerGuardOffenders(AuthorizedFileFor(WriterToken), writerCode));

        // ...and a SECOND declaring file is an offender, so the exact-presence rule
        // can never be satisfied by two files.
        Assert.NotEmpty(ScanForLedgerGuardOffenders(second, writerCode));

        // The sweep's own cardinality assertion would also fail on two.
        var declaring = new List<string> { AuthorizedFileFor(WriterToken), second };
        Assert.NotNull(Record.Exception(() => Assert.Single(declaring)));

        // ...and passes on exactly one, so the control is calibrated both ways.
        Assert.Null(Record.Exception(() => Assert.Single(new List<string> { AuthorizedFileFor(WriterToken) })));
    }

    [Theory]
    [InlineData("src/PAXCookbookSetup/ServiceOwnershipLedgerWriter.cs")]
    [InlineData("src/PAXCookbook.Shared/Contracts/ServiceOwnershipLedgerWriter.cs")]
    [InlineData("src/PAXCookbookSetup/Service/ServiceOwnershipLedgerWriterCore.cs")]
    public void MUTATION_the_writer_at_an_alternate_path_is_caught(string alternate)
    {
        string probe = IndependentlyPinnedFullPath(alternate);
        Assert.NotEqual(AuthorizedFileFor(WriterToken), probe, StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(ScanForLedgerGuardOffenders(
            probe, "class ServiceOwnershipLedgerWriter { }"));
    }

    [Theory]
    [InlineData(WriterToken)]
    [InlineData(ExecutorToken)]
    public void The_reader_token_inside_the_writer_or_executor_file_is_an_offender(string authorizedToken)
    {
        string code = "class X { void M() { var y = " + ReaderToken + ".Read(); } }";

        Assert.NotEmpty(ScanForLedgerGuardOffenders(AuthorizedFileFor(authorizedToken), code));
    }

    [Theory]
    [InlineData(WriterToken)]
    [InlineData(ExecutorToken)]
    public void Neither_new_file_mentions_any_reader_type_even_in_a_comment(string authorizedToken)
    {
        // STRICTER THAN THE GUARD ON PURPOSE. The guard strips comments; this reads
        // the RAW bytes, because a comment naming a reader type in these two files
        // would be a design claim the containment boundary does not support.
        string raw = File.ReadAllText(AuthorizedFileFor(authorizedToken));

        foreach (string readerType in new[]
                 {
                     "ServiceOwnershipLedgerReader",
                     "ServiceOwnershipLedgerReaderInterpreter",
                     "ServiceOwnershipLedgerReadResult",
                     "ServiceOwnershipLedgerReadState",
                     "ServiceOwnershipLedgerReadFacts",
                 })
        {
            Assert.DoesNotContain(readerType, raw, StringComparison.Ordinal);
        }

        // POSITIVE CONTROL: the raw scan really can find a token in this same file.
        Assert.Contains("PAXCookbookSetup.Service", raw, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WriterToken, "OwnershipLedgerObserver")]
    [InlineData(WriterToken, "ServiceOwnershipLedgerStore")]
    [InlineData(WriterToken, "ServiceOwnershipLedgerRepository")]
    [InlineData(ExecutorToken, "OwnershipLedgerObserver")]
    [InlineData(ExecutorToken, "ServiceOwnershipLedgerStore")]
    [InlineData(ExecutorToken, "ServiceOwnershipLedgerRepository")]
    public void An_observer_store_or_repository_inside_the_new_files_is_an_offender(
        string authorizedToken, string forbiddenToken)
    {
        Assert.Contains(forbiddenToken, GloballyForbiddenLedgerTokens, StringComparer.Ordinal);

        string code = "class X { " + forbiddenToken + " f; }";

        Assert.NotEmpty(ScanForLedgerGuardOffenders(AuthorizedFileFor(authorizedToken), code));

        // ...and the real files really do not carry it.
        string realCode = StripComments(File.ReadAllText(AuthorizedFileFor(authorizedToken)));
        Assert.DoesNotContain(forbiddenToken, realCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MUTATION_inserting_the_executor_token_into_the_writer_file_is_caught()
    {
        string code = "class X { " + ExecutorToken + " f; }";

        Assert.NotEmpty(ScanForLedgerGuardOffenders(AuthorizedFileFor(WriterToken), code));
        Assert.NotEmpty(ScanForLedgerGuardOffenders(AllowedReaderFullPath(), code));

        // ...and it is permitted ONLY in its own file, which is why ruling 4's zero
        // is a deliberate choice rather than an accident of the rule.
        Assert.Empty(ScanForLedgerGuardOffenders(AuthorizedFileFor(ExecutorToken), code));
    }

    // ===========================================================================
    // FIXTURE BUILDERS
    // ===========================================================================

    // A STRUCTURALLY VALID prior descriptor (cycle 48, schema v3): owner
    // BUILTIN\Administrators, a captured group SID, DACL present+protected, no
    // SACL, and EXACTLY the two measured ACEs (SYSTEM then BUILTIN\Administrators,
    // AccessAllowed, AceFlags ObjectInherit|ContainerInherit, FileFullControlRights).
    private static readonly byte[] PriorBytes =
        { 0x01, 0x00, 0x04, 0x90, 0x14, 0x00, 0x00, 0x00, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00, 0x20, 0x02, 0x00, 0x00, 0x01, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x15, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0xE9, 0x03, 0x00, 0x00, 0x02, 0x00, 0x34, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x03, 0x14, 0x00, 0xFF, 0x01, 0x1F, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x12, 0x00, 0x00, 0x00, 0x00, 0x03, 0x18, 0x00, 0xFF, 0x01, 0x1F, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00, 0x20, 0x02, 0x00, 0x00 };

    private static string PriorB64 => Convert.ToBase64String(PriorBytes);

    private static string PriorSha => Hex(SHA256.HashData(PriorBytes));

    private static string Hex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static void Refused(string json, ServiceOwnershipLedgerInvalidReason expected)
    {
        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(json);
        Assert.True(result.IsRefused, "expected a refusal but got " + result.Outcome);
        Assert.Equal(expected, result.Reason);
        Assert.Null(result.Document);
    }

    private sealed class EntryFixture
    {
        public string Provider = ProviderToken;
        public string Profile = ProfileToken;
        public string Mechanism = BackingFileMechanismToken;
        public string Mask = ApprovedMaskText;
        public string PolicyVersionRaw = "3";
        public string Lifecycle = "intended";
        public string ProviderUniqueName = ProviderUniqueNameValue;
        public string KeyStorageRoot = KeyStorageRootToken;
        public string DescriptorFormat = DescriptorFormatToken;
        public string? BindingOverride;
        public string? OmitProperty;
    }

    private sealed class DocFixture
    {
        public string SchemaVersionRaw = "3";
        public string TransactionState = "preparing";
        public EntryFixture Entry = new();
    }

    private static DocFixture PopulatedDocument() => new();

    private static string Q(string value) => "\"" + value + "\"";

    private static string EmptyDocument(string schemaVersion)
    {
        var sb = new StringBuilder("{");
        sb.Append("\"schemaVersion\":").Append(schemaVersion).Append(',');
        sb.Append("\"productOwnershipMarker\":")
          .Append(Q(ServiceOwnershipLedgerContract.ProductOwnershipMarker)).Append(',');
        sb.Append("\"managedFeatureId\":")
          .Append(Q(ServiceOwnershipLedgerContract.ManagedFeatureId)).Append(',');
        sb.Append("\"installationOwnershipId\":\"install-0001\",");
        sb.Append("\"generation\":1,");
        sb.Append("\"transactionState\":\"idle\",");
        sb.Append("\"entries\":[],");
        sb.Append("\"createdUtc\":").Append(Q(Stamp)).Append(',');
        sb.Append("\"updatedUtc\":").Append(Q(Stamp)).Append(',');
        sb.Append("\"lastOperationId\":\"op-0001\"}");
        return sb.ToString();
    }

    private static string Build(DocFixture doc)
    {
        var sb = new StringBuilder("{");
        sb.Append("\"schemaVersion\":").Append(doc.SchemaVersionRaw).Append(',');
        sb.Append("\"productOwnershipMarker\":")
          .Append(Q(ServiceOwnershipLedgerContract.ProductOwnershipMarker)).Append(',');
        sb.Append("\"managedFeatureId\":")
          .Append(Q(ServiceOwnershipLedgerContract.ManagedFeatureId)).Append(',');
        sb.Append("\"installationOwnershipId\":\"install-0001\",");
        sb.Append("\"generation\":1,");
        sb.Append("\"transactionState\":").Append(Q(doc.TransactionState)).Append(',');
        sb.Append("\"entries\":[").Append(BuildEntry(doc.Entry)).Append("],");
        sb.Append("\"createdUtc\":").Append(Q(Stamp)).Append(',');
        sb.Append("\"updatedUtc\":").Append(Q(Stamp)).Append(',');
        sb.Append("\"lastOperationId\":\"op-0001\"}");
        return sb.ToString();
    }

    private static string BuildEntry(EntryFixture e)
    {
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.Provider, out ServiceOwnershipPrivateKeyProviderKind provider);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.Profile, out ServiceOwnershipRightsProfileId profile);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.Mechanism, out ServiceOwnershipGrantMechanism mechanism);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.KeyStorageRoot, out ServiceOwnershipKeyStorageRoot keyStorageRoot);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.DescriptorFormat, out ServiceOwnershipDescriptorFormat descriptorFormat);

        string binding = e.BindingOverride ?? ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            1, OwnerSid, SvcSid, Thumb, provider, profile, KeyId, mechanism, e.Mask,
            ServiceOwnershipPriorDaclState.Present, PriorSha,
            e.ProviderUniqueName, keyStorageRoot, descriptorFormat);

        var parts = new List<(string Name, string Raw)>
        {
            ("entryId", "\"entry-0001\""),
            ("owningUserSid", Q(OwnerSid)),
            ("serviceSid", Q(SvcSid)),
            ("credentialKind", "\"personal-app-registration-certificate\""),
            ("certificateThumbprintSha1", Q(Thumb)),
            ("provenance", "\"referenced\""),
            ("privateKeyProviderKind", Q(e.Provider)),
            ("rightsProfileId", Q(e.Profile)),
            ("keyIdentity", Q(KeyId)),
            ("grantMechanism", Q(e.Mechanism)),
            ("grantedRightsMask", Q(e.Mask)),
            ("rightsPolicyVersion", e.PolicyVersionRaw),
            ("priorDaclState", "\"present\""),
            ("priorDaclBytesBase64", Q(PriorB64)),
            ("priorDaclSha256", Q(PriorSha)),
            ("capturedStateBindingSha256", Q(binding)),
            ("associatedPromotedJobIds", "[\"job-0001\"]"),
            ("lifecycleState", Q(e.Lifecycle)),
            ("createdUtc", Q(Stamp)),
            ("updatedUtc", Q(Stamp)),
            ("providerUniqueName", Q(e.ProviderUniqueName)),
            ("keyStorageRoot", Q(e.KeyStorageRoot)),
            ("descriptorFormat", Q(e.DescriptorFormat)),
        };

        var sb = new StringBuilder("{");
        bool first = true;
        foreach ((string name, string raw) in parts)
        {
            if (name == e.OmitProperty)
            {
                continue;
            }
            if (!first)
            {
                sb.Append(',');
            }
            first = false;
            sb.Append(Q(name)).Append(':').Append(raw);
        }
        return sb.Append('}').ToString();
    }

    private static string ContractPath()
    {
        string path = Path.Combine(
            RepoRoot(), "src", "PAXCookbook.Shared", "Contracts", "ServiceOwnershipLedgerContract.cs");
        Assert.True(File.Exists(path), path);
        return path;
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PAXCookbook.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        bool inBlock = false;
        foreach (string line in source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string current = line;
            var kept = new StringBuilder(current.Length);
            for (int i = 0; i < current.Length; i++)
            {
                if (inBlock)
                {
                    if (i + 1 < current.Length && current[i] == '*' && current[i + 1] == '/')
                    {
                        inBlock = false;
                        i++;
                    }
                    continue;
                }
                if (i + 1 < current.Length && current[i] == '/' && current[i + 1] == '*')
                {
                    inBlock = true;
                    i++;
                    continue;
                }
                if (i + 1 < current.Length && current[i] == '/' && current[i + 1] == '/')
                {
                    break;
                }
                kept.Append(current[i]);
            }
            sb.Append(kept).Append('\n');
        }
        return sb.ToString();
    }
}
