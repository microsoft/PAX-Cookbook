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

// Cycle 38a - SERVICE OWNERSHIP LEDGER schema contract; updated by cycle 42 to
// schema v2 (provider-specific rights profile binding).
//
// SCOPE, stated plainly. This file exercises a PURE, PORTABLE, self-contained
// parser/validator. Nothing here opens a certificate store, touches a private key,
// reads or writes an ACL, reads the registry, reads Windows Credential Manager,
// installs/starts/stops a service, elevates, reads or writes ProgramData, opens a
// socket, or touches the filesystem THROUGH THE CONTRACT. The only file the tests
// themselves read is the contract's own SOURCE TEXT, for structural containment.
//
// THE HOMONYM, stated once and never blurred. Two INDEPENDENT artifacts happen to
// share the leaf name "ownership-ledger.json":
//   * ProvisioningContract.LedgerFileName - the ManagedChefKeys ledger, under
//     %CommonApplicationData%\PAXCookbook\ManagedChefKeys, with live readers and
//     writers in Setup.
//   * ServiceOwnershipLedgerContract.LedgerFileName - the service credential
//     ownership ledger, under %CommonApplicationData%\PAXCookbook\Service, with no
//     reader and no writer anywhere.
// They are COINCIDENTAL HOMONYMS. Neither is derived from the other, neither is
// single-sourced from the other, and each validator REJECTS the other's document.
// Both directions are proven below.
public class ServiceOwnershipLedgerContractTests
{
    // ---- shared synthetic fixture values -------------------------------------
    //
    // Every value below is synthetic. No real tenant, account, certificate, key,
    // machine, or directory is represented.

    private const string OwnerSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string OwnerSid2 = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string SvcSid = "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890";
    private const string Thumb = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    private const string Thumb2 = "0123456789ABCDEF0123456789ABCDEF01234567";
    private const string KeyId = "synthetic-key-identity_01.test";
    private const string Mask = "00000081";
    private const string ProviderToken = "microsoft-software-key-storage-provider";
    private const string RetiredProfileToken =
        "microsoft-software-ksp-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.0";
    private const string ProfileToken =
        "microsoft-software-ksp-backing-file-rsa2048-ps256-azure-identity-1.18.0-msal-4.82.1-graph-auth-2.39.0-filesystemrights";
    private const string MechanismToken = "microsoft-software-ksp-backing-file-dacl";
    private const string RetiredMechanismToken = "cng-security-descriptor";
    private const string DescriptorFormatToken = "microsoft-software-ksp-backing-file-self-relative-v1";
    private const string KeyStorageRootToken = "microsoft-software-key-storage-provider-machine-keys";
    private const string ProviderUniqueNameValue = "synthetic-unique-leaf_01.pvk";
    private const string Stamp = "2026-08-06T00:00:00Z";
    private const string LaterStamp = "2026-08-06T00:00:01Z";

    // These are CONST fields on purpose. A const initializer is bound during
    // DECLARATION binding, so the ownership marker and the rights-mask vocabulary
    // are demanded from the contract before any method body is ever compiled.
    private const string PinnedProductOwnershipMarker =
        ServiceOwnershipLedgerContract.ProductOwnershipMarker;
    private const string PinnedManagedFeatureId =
        ServiceOwnershipLedgerContract.ManagedFeatureId;
    private const uint PinnedKnownRightsBits =
        ServiceOwnershipLedgerContract.KnownRightsBits;
    private const uint PinnedProhibitedManagementRightsBits =
        ServiceOwnershipLedgerContract.ProhibitedManagementRightsBits;
    private const uint PinnedFullControlRights =
        ServiceOwnershipLedgerContract.FullControlRights;

    [Fact]
    public void The_compile_time_pins_match_the_contract()
    {
        Assert.Equal("PAXCookbook.ServiceOwnership.v1", PinnedProductOwnershipMarker);
        Assert.Equal("service-credential-ownership", PinnedManagedFeatureId);
        Assert.Equal(0xF01F019Bu, PinnedKnownRightsBits);
        Assert.Equal(0x500D0000u, PinnedProhibitedManagementRightsBits);
        Assert.Equal(2032027u, PinnedFullControlRights);
    }

    // A STRUCTURALLY VALID prior descriptor (cycle 48, schema v3): owner
    // BUILTIN\Administrators, a captured group SID, DACL present+protected, no
    // SACL, and EXACTLY the two measured ACEs (SYSTEM then BUILTIN\Administrators,
    // AccessAllowed, AceFlags ObjectInherit|ContainerInherit, FileFullControlRights).
    // Built independently of the contract via a standalone script and re-verified
    // by the contract's own structural parser in the tests below.
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

    // ===========================================================================
    // FIXED CAPTURED-STATE BINDING TEST VECTOR
    // ===========================================================================
    //
    // Hard-coded input, hard-coded expected digest. The digest was computed
    // INDEPENDENTLY of the contract implementation (PowerShell + SHA256 over the
    // UTF-8 bytes of the exact preimage) before the contract existed, so it pins
    // the preimage grammar rather than restating whatever the code happens to do.

    private const string VectorOwnerSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string VectorServiceSid = "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890";
    private const string VectorThumbprint = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    private const string VectorKeyIdentity = "synthetic-key-identity_01.test";
    private const string VectorMask = "00120009";
    private const string VectorPriorSha256 = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const int VectorGeneration = 7;
    private const string VectorProviderUniqueName = "synthetic-unique-leaf_01.pvk";

    private const string VectorExpectedBinding =
        "F154942C417352D55984C33709CEF8521E39A7AF9DCD9BCC1AE3FF72EF96FC87";

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
    public void The_fixed_binding_vector_reproduces_its_hard_coded_preimage_and_digest()
    {
        string preimage = ServiceOwnershipLedgerContract.BuildCapturedStateBindingPreimage(
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

        Assert.Equal(VectorExpectedPreimage, preimage);

        // Separator is LF only, never CRLF, and there is no trailing newline.
        Assert.DoesNotContain("\r", preimage, StringComparison.Ordinal);
        Assert.False(preimage.EndsWith("\n", StringComparison.Ordinal));
        Assert.Equal(15, preimage.Split('\n').Length);

        // UTF-8, no BOM. 869 bytes was measured independently of the contract.
        byte[] bytes = new UTF8Encoding(false).GetBytes(preimage);
        Assert.Equal(869, bytes.Length);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);

        string binding = ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
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

        Assert.Equal(VectorExpectedBinding, binding);
        Assert.Equal(64, binding.Length);
        Assert.Equal(binding.ToUpperInvariant(), binding);
        Assert.Equal(Hex(SHA256.HashData(bytes)), binding);
    }

    [Fact]
    public void The_binding_prefix_and_field_order_are_pinned()
    {
        Assert.Equal(
            "PAXCookbook.ServiceOwnership.Binding.v3",
            ServiceOwnershipLedgerContract.BindingPreimagePrefix);

        Assert.Equal(
            new[]
            {
                "generation", "owningUserSid", "serviceSid", "certificateThumbprintSha1",
                "privateKeyProviderKind", "rightsProfileId", "keyIdentity", "grantMechanism",
                "grantedRightsMask", "priorDaclState", "priorDaclSha256",
                "providerUniqueName", "keyStorageRoot", "descriptorFormat",
            },
            ServiceOwnershipLedgerContract.BindingFieldOrder.ToArray());
    }

    [Fact]
    public void The_binding_escape_rule_is_implemented_even_though_the_grammars_make_it_a_no_op()
    {
        // Documented plainly: the constrained grammars (SID, 40-hex thumbprint,
        // allow-listed keyIdentity, 8-hex mask, closed wire tokens, 64-hex digest)
        // cannot produce a backslash, an LF, or an '='. The escape is implemented
        // and tested anyway so the preimage stays unambiguous if a grammar is ever
        // widened.
        Assert.Equal(@"a\\b", ServiceOwnershipLedgerContract.EscapeBindingValue(@"a\b"));
        Assert.Equal(@"a\nb", ServiceOwnershipLedgerContract.EscapeBindingValue("a\nb"));
        Assert.Equal(@"a\x3Db", ServiceOwnershipLedgerContract.EscapeBindingValue("a=b"));

        // Backslash is escaped FIRST, so an escape sequence can never be forged.
        Assert.Equal(@"a\\nb", ServiceOwnershipLedgerContract.EscapeBindingValue(@"a\nb"));
        Assert.Equal(string.Empty, ServiceOwnershipLedgerContract.EscapeBindingValue(null));

        // A real fixture value is unaffected.
        Assert.Equal(KeyId, ServiceOwnershipLedgerContract.EscapeBindingValue(KeyId));
    }

    [Theory]
    [InlineData(ServiceOwnershipPriorDaclState.Absent)]
    [InlineData(ServiceOwnershipPriorDaclState.Empty)]
    public void The_binding_normalises_the_prior_hash_to_empty_when_no_payload_exists(
        ServiceOwnershipPriorDaclState state)
    {
        string withHash = ServiceOwnershipLedgerContract.BuildCapturedStateBindingPreimage(
            1, OwnerSid, SvcSid, Thumb,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId, KeyId,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl, Mask, state, PriorSha,
            ProviderUniqueNameValue, ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
            ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1);
        string withoutHash = ServiceOwnershipLedgerContract.BuildCapturedStateBindingPreimage(
            1, OwnerSid, SvcSid, Thumb,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId, KeyId,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl, Mask, state, string.Empty,
            ProviderUniqueNameValue, ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
            ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1);

        Assert.Equal(withoutHash, withHash);
        Assert.Contains("priorDaclSha256=\nproviderUniqueName=", withHash, StringComparison.Ordinal);
    }

    // ===========================================================================
    // RIGHTS VOCABULARY
    // ===========================================================================

    [Fact]
    public void Full_control_equals_2032027_and_the_or_of_the_eleven_specific_bits()
    {
        Assert.Equal(0x001F019Bu, ServiceOwnershipLedgerContract.FullControlRights);
        Assert.Equal(2032027u, ServiceOwnershipLedgerContract.FullControlRights);

        uint[] specific =
        {
            ServiceOwnershipLedgerContract.ReadDataRight,
            ServiceOwnershipLedgerContract.WriteDataRight,
            ServiceOwnershipLedgerContract.ReadExtendedAttributesRight,
            ServiceOwnershipLedgerContract.WriteExtendedAttributesRight,
            ServiceOwnershipLedgerContract.ReadAttributesRight,
            ServiceOwnershipLedgerContract.WriteAttributesRight,
            ServiceOwnershipLedgerContract.DeleteRight,
            ServiceOwnershipLedgerContract.ReadPermissionsRight,
            ServiceOwnershipLedgerContract.ChangePermissionsRight,
            ServiceOwnershipLedgerContract.TakeOwnershipRight,
            ServiceOwnershipLedgerContract.SynchronizeRight,
        };

        Assert.Equal(11, specific.Length);
        Assert.Equal(specific, ServiceOwnershipLedgerContract.SpecificRightsBits.ToArray());

        uint or = 0;
        foreach (uint bit in specific)
        {
            or |= bit;
        }
        Assert.Equal(ServiceOwnershipLedgerContract.FullControlRights, or);

        // Grounded published values, restated so a silent edit fails here.
        Assert.Equal(0x00000001u, ServiceOwnershipLedgerContract.ReadDataRight);
        Assert.Equal(0x00000002u, ServiceOwnershipLedgerContract.WriteDataRight);
        Assert.Equal(0x00000008u, ServiceOwnershipLedgerContract.ReadExtendedAttributesRight);
        Assert.Equal(0x00000010u, ServiceOwnershipLedgerContract.WriteExtendedAttributesRight);
        Assert.Equal(0x00000080u, ServiceOwnershipLedgerContract.ReadAttributesRight);
        Assert.Equal(0x00000100u, ServiceOwnershipLedgerContract.WriteAttributesRight);
        Assert.Equal(0x00010000u, ServiceOwnershipLedgerContract.DeleteRight);
        Assert.Equal(0x00020000u, ServiceOwnershipLedgerContract.ReadPermissionsRight);
        Assert.Equal(0x00040000u, ServiceOwnershipLedgerContract.ChangePermissionsRight);
        Assert.Equal(0x00080000u, ServiceOwnershipLedgerContract.TakeOwnershipRight);
        Assert.Equal(0x00100000u, ServiceOwnershipLedgerContract.SynchronizeRight);
        Assert.Equal(0x10000000u, ServiceOwnershipLedgerContract.GenericAllRight);
        Assert.Equal(0x20000000u, ServiceOwnershipLedgerContract.GenericExecuteRight);
        Assert.Equal(0x40000000u, ServiceOwnershipLedgerContract.GenericWriteRight);
        Assert.Equal(0x80000000u, ServiceOwnershipLedgerContract.GenericReadRight);
    }

    [Fact]
    public void The_three_rights_constants_stay_separate_and_correct()
    {
        // Ruling B1.3: known / prohibited / approved are THREE things, never merged.
        Assert.Equal(0xF01F019Bu, ServiceOwnershipLedgerContract.KnownRightsBits);
        Assert.Equal(0x500D0000u, ServiceOwnershipLedgerContract.ProhibitedManagementRightsBits);
        Assert.Equal(0x20000000u, ServiceOwnershipLedgerContract.NotUsedRightsBits);

        uint known = ServiceOwnershipLedgerContract.FullControlRights
            | ServiceOwnershipLedgerContract.GenericAllRight
            | ServiceOwnershipLedgerContract.GenericExecuteRight
            | ServiceOwnershipLedgerContract.GenericWriteRight
            | ServiceOwnershipLedgerContract.GenericReadRight;
        Assert.Equal(known, ServiceOwnershipLedgerContract.KnownRightsBits);

        uint prohibited = ServiceOwnershipLedgerContract.DeleteRight
            | ServiceOwnershipLedgerContract.ChangePermissionsRight
            | ServiceOwnershipLedgerContract.TakeOwnershipRight
            | ServiceOwnershipLedgerContract.GenericAllRight
            | ServiceOwnershipLedgerContract.GenericWriteRight;
        Assert.Equal(prohibited, ServiceOwnershipLedgerContract.ProhibitedManagementRightsBits);

        // Cycle 42 ruling: known / prohibited / approved remain THREE separate
        // things. An approved profile now EXISTS, and it is a strict subset of the
        // known bits that shares nothing with the prohibited or not-used bits.
        Assert.True(ServiceOwnershipLedgerContract.HasApprovedRightsProfile);
        Assert.Equal(0x00120009u, ServiceOwnershipLedgerContract.ApprovedRightsProfileMask);
        Assert.NotEqual(
            ServiceOwnershipLedgerContract.ApprovedRightsProfileMask,
            ServiceOwnershipLedgerContract.KnownRightsBits);
        Assert.Equal(
            0u,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileMask
                & ServiceOwnershipLedgerContract.ProhibitedManagementRightsBits);
        Assert.Equal(
            0u,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileMask
                & ServiceOwnershipLedgerContract.NotUsedRightsBits);
    }

    [Fact]
    public void The_mask_is_handled_as_unsigned_and_never_round_trips_through_a_signed_int()
    {
        // GenericRead is NEGATIVE as Int32; the unsigned path must survive it.
        var mask = new ServiceOwnershipRightsMask(ServiceOwnershipLedgerContract.GenericReadRight);
        Assert.Equal(0x80000000u, mask.Value);
        Assert.Equal("80000000", mask.ToWireText());
        Assert.True(mask.Value > int.MaxValue);

        AssertMaskParsesTo("80000000", mask);
        AssertMaskParsesTo("00000081", new ServiceOwnershipRightsMask(0x81u));
        AssertMaskParsesTo("00100000", new ServiceOwnershipRightsMask(
            ServiceOwnershipLedgerContract.SynchronizeRight));
    }

    // Declared with the mask type in the SIGNATURE so the required mask surface is
    // demanded at declaration binding, not only inside a method body.
    private static void AssertMaskParsesTo(string text, ServiceOwnershipRightsMask expected)
    {
        Assert.True(ServiceOwnershipLedgerContract.TryValidateRightsMask(
            text, out ServiceOwnershipRightsMask parsed, out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, reason);
        Assert.Equal(expected, parsed);
        Assert.Equal(text, parsed.ToWireText());
    }

    [Theory]
    [InlineData("0000008b")]           // lowercase hex
    [InlineData("0081")]               // wrong length (short)
    [InlineData("000000081")]          // wrong length (long)
    [InlineData("0x000081")]           // 0x prefix
    [InlineData("0000008G")]           // non-hex
    [InlineData("        ")]           // whitespace
    [InlineData("")]                   // empty
    [InlineData(null)]                 // null
    public void A_malformed_rights_mask_is_refused_by_format(string? text)
    {
        Assert.False(ServiceOwnershipLedgerContract.TryValidateRightsMask(
            text, out _, out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.InvalidRightsMaskFormat, reason);
    }

    [Fact]
    public void A_zero_rights_mask_is_refused()
    {
        Assert.False(ServiceOwnershipLedgerContract.TryValidateRightsMask(
            "00000000", out _, out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.ZeroRightsMask, reason);
    }

    [Theory]
    [InlineData("00000004")]   // 0x4 is not a published CryptoKeyRights member
    [InlineData("00000020")]   // 0x20
    [InlineData("08000000")]   // 0x08000000
    public void An_unknown_rights_bit_is_refused(string text)
    {
        Assert.False(ServiceOwnershipLedgerContract.TryValidateRightsMask(
            text, out _, out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.UnknownRightsBit, reason);
    }

    [Theory]
    [InlineData("00010000")]   // Delete
    [InlineData("00040000")]   // ChangePermissions
    [InlineData("00080000")]   // TakeOwnership
    [InlineData("10000000")]   // GenericAll
    [InlineData("40000000")]   // GenericWrite
    [InlineData("20000000")]   // GenericExecute - published description is "Not used."
    [InlineData("001F019B")]   // FullControl carries three prohibited bits
    public void A_prohibited_management_or_not_used_bit_is_refused(string text)
    {
        Assert.False(ServiceOwnershipLedgerContract.TryValidateRightsMask(
            text, out _, out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.ProhibitedManagementRight, reason);
    }

    [Fact]
    public void The_mask_validation_order_is_format_then_zero_then_unknown_then_prohibited()
    {
        // A value carrying BOTH an unknown bit and a prohibited bit reports the
        // UNKNOWN bit, proving the ordering rather than an incidental match.
        Assert.False(ServiceOwnershipLedgerContract.TryValidateRightsMask(
            "10000004", out _, out ServiceOwnershipLedgerInvalidReason reason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.UnknownRightsBit, reason);
    }

    [Fact]
    public void Exactly_one_rights_combination_can_be_authorised_for_activation()
    {
        uint[] specific = ServiceOwnershipLedgerContract.SpecificFileRightsBits.ToArray();
        Assert.Equal(14, specific.Length);
        int authorised = 0;
        uint authorisedValue = 0u;
        int missingBit = 0;
        int notPermitted = 0;

        for (int subset = 0; subset < (1 << 14); subset++)
        {
            uint value = 0;
            for (int bit = 0; bit < specific.Length; bit++)
            {
                if ((subset & (1 << bit)) != 0)
                {
                    value |= specific[bit];
                }
            }
            if (value == 0u)
            {
                continue;
            }

            bool ok = ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
                new ServiceOwnershipRightsMask(value),
                ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
                ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
                ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
                3,
                ServiceOwnershipLedgerContract.ApprovedDescriptorFormat,
                out ServiceOwnershipLedgerInvalidReason reason);

            if (ok)
            {
                authorised++;
                authorisedValue = value;
                Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, reason);
            }
            else
            {
                Assert.NotEqual(ServiceOwnershipLedgerInvalidReason.None, reason);
                if (reason == ServiceOwnershipLedgerInvalidReason.ApprovedRightsProfileBitMissing)
                {
                    missingBit++;
                }
                if (reason == ServiceOwnershipLedgerInvalidReason.ApprovedRightsProfileBitNotPermitted)
                {
                    notPermitted++;
                }
            }
        }

        Assert.Equal(1, authorised);
        Assert.Equal(0x00120009u, authorisedValue);

        // The loop is not vacuous: both exactness refusals really are exercised.
        Assert.True(missingBit > 0);
        Assert.True(notPermitted > 0);
    }

    [Fact]
    public void No_rights_combination_can_be_authorised_under_a_broad_provider_classification()
    {
        uint[] specific = ServiceOwnershipLedgerContract.SpecificFileRightsBits.ToArray();
        int authorised = 0;
        int retiredRefusals = 0;

        for (int subset = 0; subset < (1 << 14); subset++)
        {
            uint value = 0;
            for (int bit = 0; bit < specific.Length; bit++)
            {
                if ((subset & (1 << bit)) != 0)
                {
                    value |= specific[bit];
                }
            }
            if (value == 0u)
            {
                continue;
            }

            bool ok = ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
                new ServiceOwnershipRightsMask(value),
                ServiceOwnershipPrivateKeyProviderKind.Cng,
                ServiceOwnershipGrantMechanism.CngSecurityDescriptor,
                ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
                3,
                ServiceOwnershipLedgerContract.ApprovedDescriptorFormat,
                out ServiceOwnershipLedgerInvalidReason reason);

            if (ok)
            {
                authorised++;
            }
            Assert.NotEqual(ServiceOwnershipLedgerInvalidReason.None, reason);
            if (reason == ServiceOwnershipLedgerInvalidReason.RetiredCngSecurityDescriptorMechanismUnsupported)
            {
                retiredRefusals++;
            }
        }

        Assert.Equal(0, authorised);
        Assert.Equal((1 << 14) - 1, retiredRefusals);
    }

    [Fact]
    public void Activation_refusals_name_the_specific_blocking_reason()
    {
        var approved = new ServiceOwnershipRightsMask(
            ServiceOwnershipLedgerContract.ApprovedRightsProfileMask);
        ServiceOwnershipDescriptorFormat format = ServiceOwnershipLedgerContract.ApprovedDescriptorFormat;

        Assert.False(ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
            approved, ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.CngSecurityDescriptor,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId, 3, format,
            out ServiceOwnershipLedgerInvalidReason retiredReason));
        Assert.Equal(
            ServiceOwnershipLedgerInvalidReason.RetiredCngSecurityDescriptorMechanismUnsupported,
            retiredReason);

        Assert.False(ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
            approved, ServiceOwnershipPrivateKeyProviderKind.LegacyCsp,
            ServiceOwnershipGrantMechanism.CspKeyFileDacl,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId, 3, format,
            out ServiceOwnershipLedgerInvalidReason cspReason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.UnsupportedProviderRightsVocabulary, cspReason);

        Assert.False(ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
            approved, ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.CspKeyFileDacl,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId, 3, format,
            out ServiceOwnershipLedgerInvalidReason pairReason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.ProviderMechanismMismatch, pairReason);

        Assert.False(ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
            new ServiceOwnershipRightsMask(0u),
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId, 3, format,
            out ServiceOwnershipLedgerInvalidReason zeroReason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.ZeroRightsMask, zeroReason);

        Assert.False(ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
            approved, ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            ServiceOwnershipRightsProfileId.Unspecified, 3, format,
            out ServiceOwnershipLedgerInvalidReason profileReason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.NoApprovedRightsProfile, profileReason);

        Assert.False(ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
            approved, ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId, 1, format,
            out ServiceOwnershipLedgerInvalidReason versionReason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.InvalidRightsPolicyVersion, versionReason);

        Assert.False(ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
            approved, ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId, 3, ServiceOwnershipDescriptorFormat.Unspecified,
            out ServiceOwnershipLedgerInvalidReason formatReason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.InvalidDescriptorFormat, formatReason);

        // POSITIVE CONTROL: the one authorised combination really does succeed, so
        // the refusals above are not produced by a gate that always refuses.
        Assert.True(ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
            approved, ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId, 3, format,
            out ServiceOwnershipLedgerInvalidReason okReason));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, okReason);
    }

    [Fact]
    public void Both_cng_kinds_share_the_rights_vocabulary_but_only_one_carries_the_profile()
    {
        Assert.True(ServiceOwnershipLedgerContract.IsRightsVocabularySupported(
            ServiceOwnershipPrivateKeyProviderKind.Cng));
        Assert.True(ServiceOwnershipLedgerContract.IsRightsVocabularySupported(
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider));
        Assert.False(ServiceOwnershipLedgerContract.IsRightsVocabularySupported(
            ServiceOwnershipPrivateKeyProviderKind.LegacyCsp));
        Assert.False(ServiceOwnershipLedgerContract.IsRightsVocabularySupported(
            ServiceOwnershipPrivateKeyProviderKind.Unspecified));

        Assert.True(ServiceOwnershipLedgerContract.IsExactApprovedProviderKind(
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider));
        Assert.False(ServiceOwnershipLedgerContract.IsExactApprovedProviderKind(
            ServiceOwnershipPrivateKeyProviderKind.Cng));
        Assert.False(ServiceOwnershipLedgerContract.IsExactApprovedProviderKind(
            ServiceOwnershipPrivateKeyProviderKind.LegacyCsp));

        Assert.True(ServiceOwnershipLedgerContract.IsMechanismPairedWith(
            ServiceOwnershipPrivateKeyProviderKind.Cng,
            ServiceOwnershipGrantMechanism.CngSecurityDescriptor));
        Assert.True(ServiceOwnershipLedgerContract.IsMechanismPairedWith(
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.CngSecurityDescriptor));
        Assert.True(ServiceOwnershipLedgerContract.IsMechanismPairedWith(
            ServiceOwnershipPrivateKeyProviderKind.LegacyCsp,
            ServiceOwnershipGrantMechanism.CspKeyFileDacl));
        Assert.False(ServiceOwnershipLedgerContract.IsMechanismPairedWith(
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.CspKeyFileDacl));
        Assert.False(ServiceOwnershipLedgerContract.IsMechanismPairedWith(
            ServiceOwnershipPrivateKeyProviderKind.Cng,
            ServiceOwnershipGrantMechanism.CspKeyFileDacl));
        Assert.False(ServiceOwnershipLedgerContract.IsMechanismPairedWith(
            ServiceOwnershipPrivateKeyProviderKind.LegacyCsp,
            ServiceOwnershipGrantMechanism.CngSecurityDescriptor));
        Assert.False(ServiceOwnershipLedgerContract.IsMechanismPairedWith(
            ServiceOwnershipPrivateKeyProviderKind.Unspecified,
            ServiceOwnershipGrantMechanism.Unspecified));
    }

    // ===========================================================================
    // IDENTITY, BOUNDS AND HOMONYM PINNING
    // ===========================================================================

    [Fact]
    public void The_service_ledger_identity_constants_are_pinned_and_distinct()
    {
        Assert.Equal(3, ServiceOwnershipLedgerContract.LedgerSchemaVersion);
        Assert.Equal("PAXCookbook.ServiceOwnership.v1", ServiceOwnershipLedgerContract.ProductOwnershipMarker);
        Assert.Equal("service-credential-ownership", ServiceOwnershipLedgerContract.ManagedFeatureId);
        Assert.Equal(3, ServiceOwnershipLedgerContract.RightsPolicyVersion);
    }

    [Fact]
    public void The_bounds_are_pinned()
    {
        Assert.Equal(262144, ServiceOwnershipLedgerContract.MaxLedgerBytes);
        Assert.Equal(64, ServiceOwnershipLedgerContract.MaxEntries);
        Assert.Equal(64, ServiceOwnershipLedgerContract.MaxJobIdsPerEntry);
        Assert.Equal(128, ServiceOwnershipLedgerContract.MaxStringLength);
        Assert.Equal(128, ServiceOwnershipLedgerContract.MaxKeyIdentityLength);
        Assert.Equal(65536, ServiceOwnershipLedgerContract.MaxPriorDaclDecodedBytes);
        Assert.Equal(87400, ServiceOwnershipLedgerContract.MaxPriorDaclBase64Length);
        Assert.Equal(191, ServiceOwnershipLedgerContract.MaxSidLength);
        Assert.Equal(15, ServiceOwnershipLedgerContract.MaxSidSubAuthorities);
    }

    [Fact]
    public void The_two_ledger_file_names_are_coincidental_homonyms_and_nothing_more()
    {
        const string message =
            "COINCIDENTAL HOMONYMY, NOT SHARED IDENTITY. ProvisioningContract.LedgerFileName " +
            "(ManagedChefKeys, under %CommonApplicationData%\\PAXCookbook\\ManagedChefKeys) and " +
            "ServiceOwnershipLedgerContract.LedgerFileName (service credential ownership, under " +
            "%CommonApplicationData%\\PAXCookbook\\Service) are two DIFFERENT files in DIFFERENT " +
            "directories with DIFFERENT schemas. Neither constant is derived from the other and " +
            "neither is single-sourced from the other.";

        Assert.True("ownership-ledger.json" == ProvisioningContract.LedgerFileName, message);
        Assert.True("ownership-ledger.json" == ServiceOwnershipLedgerContract.LedgerFileName, message);
        Assert.True(
            ProvisioningContract.LedgerFileName == ServiceOwnershipLedgerContract.LedgerFileName,
            message);

        // The identities the two ledgers actually prove ownership by are DIFFERENT.
        Assert.NotEqual(
            ProvisioningContract.ProductOwnershipMarker,
            ServiceOwnershipLedgerContract.ProductOwnershipMarker);
        Assert.NotEqual(
            ProvisioningContract.ManagedFeatureId,
            ServiceOwnershipLedgerContract.ManagedFeatureId);
    }

    // ===========================================================================
    // TWO-WAY CROSS REJECTION (ruling A1.6)
    // ===========================================================================

    private static string ManagedChefKeysLedgerJson()
    {
        string hash = new string('A', 64);
        return "{" +
            "\"ledgerSchemaVersion\":1," +
            "\"productOwnershipMarker\":\"" + ProvisioningContract.ProductOwnershipMarker + "\"," +
            "\"managedFeatureId\":\"" + ProvisioningContract.ManagedFeatureId + "\"," +
            "\"currentGeneration\":1," +
            "\"currentInventorySha256\":\"" + hash + "\"," +
            "\"previousGeneration\":null," +
            "\"previousInventorySha256\":null," +
            "\"transactionState\":\"idle\"," +
            "\"ownedFileNames\":[\"" + ProvisioningContract.InventoryFileName + "\",\"" +
                ProvisioningContract.LedgerFileName + "\",\"" +
                ProvisioningContract.PreviousInventoryFileName + "\"]," +
            "\"aclPolicyVersion\":1," +
            "\"createdUtc\":\"" + Stamp + "\"," +
            "\"updatedUtc\":\"" + Stamp + "\"," +
            "\"lastOperationId\":\"op-0001\"" +
            "}";
    }

    [Fact]
    public void A_well_formed_managed_chef_keys_ledger_is_still_a_valid_managed_chef_keys_ledger()
    {
        // POSITIVE CONTROL. Without this, the cross-rejection below could pass for
        // the trivial reason that the fixture is simply broken.
        OwnershipLedgerValidationResult own = OwnershipLedgerValidator.Validate(ManagedChefKeysLedgerJson());
        Assert.True(own.IsValid);
        Assert.Equal(OwnershipLedgerInvalidReason.None, own.Reason);
    }

    [Fact]
    public void The_service_validator_rejects_a_well_formed_managed_chef_keys_ledger()
    {
        ServiceOwnershipLedgerValidationResult result =
            ServiceOwnershipLedgerValidator.Validate(ManagedChefKeysLedgerJson());

        Assert.True(result.IsRefused);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.UnknownProperty, result.Reason);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Malformed, result.Outcome);
        Assert.Null(result.Document);
    }

    [Fact]
    public void The_managed_chef_keys_validator_rejects_a_well_formed_service_ledger()
    {
        string serviceJson = ValidEmptyDocumentJson();

        // POSITIVE CONTROL: the fixture really is a valid SERVICE ledger.
        ServiceOwnershipLedgerValidationResult mine = ServiceOwnershipLedgerValidator.Validate(serviceJson);
        Assert.True(mine.IsAccepted);

        OwnershipLedgerValidationResult theirs = OwnershipLedgerValidator.Validate(serviceJson);
        Assert.False(theirs.IsValid);
        Assert.Equal(OwnershipLedgerInvalidReason.UnknownField, theirs.Reason);
        Assert.Null(theirs.Ledger);
    }

    // ===========================================================================
    // DOCUMENT / ENTRY SHAPE
    // ===========================================================================

    [Fact]
    public void The_document_property_set_is_closed_at_exactly_ten_names()
    {
        Assert.Equal(
            new[]
            {
                "schemaVersion", "productOwnershipMarker", "managedFeatureId",
                "installationOwnershipId", "generation", "transactionState", "entries",
                "createdUtc", "updatedUtc", "lastOperationId",
            },
            ServiceOwnershipLedgerContract.DocumentPropertyNames.ToArray());
    }

    [Fact]
    public void The_entry_property_set_is_closed_at_exactly_twenty_three_names()
    {
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
            ServiceOwnershipLedgerContract.EntryPropertyNames.ToArray());
        Assert.Equal(23, ServiceOwnershipLedgerContract.EntryPropertyNames.Count);
    }

    // ===========================================================================
    // ACCEPTED SHAPES
    // ===========================================================================

    [Fact]
    public void An_absent_ledger_is_only_reachable_through_the_explicit_absent_factory()
    {
        ServiceOwnershipLedgerValidationResult absent = ServiceOwnershipLedgerValidator.ForAbsentLedger();

        Assert.Equal(ServiceOwnershipLedgerOutcome.Absent, absent.Outcome);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, absent.Reason);
        Assert.True(absent.IsAccepted);
        Assert.Null(absent.Document);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Null_or_empty_json_is_malformed_and_never_absent(string? json)
    {
        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(json);

        Assert.Equal(ServiceOwnershipLedgerOutcome.Malformed, result.Outcome);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.MalformedJson, result.Reason);
        Assert.NotEqual(ServiceOwnershipLedgerOutcome.Absent, result.Outcome);
        Assert.True(result.IsRefused);
    }

    [Fact]
    public void A_valid_empty_ledger_is_accepted()
    {
        ServiceOwnershipLedgerValidationResult result =
            ServiceOwnershipLedgerValidator.Validate(ValidEmptyDocumentJson());

        Assert.True(result.IsAccepted);
        Assert.Equal(ServiceOwnershipLedgerOutcome.ValidEmpty, result.Outcome);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, result.Reason);
        Assert.NotNull(result.Document);
        Assert.Empty(result.Document!.Entries);
        Assert.Equal(ServiceOwnershipLedgerContract.ProductOwnershipMarker, result.Document.ProductOwnershipMarker);
        Assert.Equal(ServiceOwnershipLedgerContract.ManagedFeatureId, result.Document.ManagedFeatureId);
        Assert.Equal(ServiceOwnershipTransactionState.Idle, result.Document.TransactionState);
    }

    [Fact]
    public void A_representative_intended_entry_is_accepted_and_never_reports_active()
    {
        ServiceOwnershipLedgerValidationResult result =
            ServiceOwnershipLedgerValidator.Validate(Build(Doc()));

        Assert.True(result.IsAccepted);
        Assert.Equal(ServiceOwnershipLedgerOutcome.InProgress, result.Outcome);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, result.Reason);

        // Cycle 38b. The outcome this test used to exclude by name no longer EXISTS,
        // so the exclusion is now proven by REFLECTION over the live vocabulary
        // rather than by naming a member the compiler would have to resolve.
        Assert.DoesNotContain("ValidActive", Enum.GetNames(typeof(ServiceOwnershipLedgerOutcome)));

        ServiceOwnershipLedgerEntry entry = Assert.Single(result.Document!.Entries);
        Assert.Equal(ServiceOwnershipLifecycleState.Intended, entry.LifecycleState);
        Assert.Equal(ServiceOwnershipCredentialKind.PersonalAppRegistrationCertificate, entry.CredentialKind);
        Assert.Equal(ServiceOwnershipProvenance.Referenced, entry.Provenance);
        Assert.Equal(
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            entry.PrivateKeyProviderKind);
        Assert.Equal(ServiceOwnershipLedgerContract.ApprovedRightsProfileId, entry.RightsProfileId);
        Assert.Equal(ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl, entry.GrantMechanism);
        Assert.Equal(ServiceOwnershipPriorDaclState.Present, entry.PriorDaclState);
        Assert.Equal(0x00000081u, entry.GrantedRightsMask.Value);
        Assert.Equal(OwnerSid, entry.OwningUserSid);
        Assert.Equal(SvcSid, entry.ServiceSid);
        Assert.Equal(ProviderUniqueNameValue, entry.ProviderUniqueName);
        Assert.Equal(
            ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
            entry.KeyStorageRoot);
        Assert.Equal(
            ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1,
            entry.DescriptorFormat);
    }

    [Fact]
    public void A_stale_entry_yields_the_stale_outcome()
    {
        DocModel doc = Doc();
        doc.TransactionState = "credential-mutated";
        doc.Entries[0].LifecycleState = "stale";

        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(Build(doc));

        Assert.True(result.IsAccepted);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Stale, result.Outcome);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    public void A_prior_dacl_state_without_a_payload_is_accepted(string state)
    {
        DocModel doc = Doc();
        doc.Entries[0].PriorDaclState = state;
        doc.Entries[0].PriorDaclBytesBase64 = string.Empty;
        doc.Entries[0].PriorDaclSha256 = string.Empty;

        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(Build(doc));

        Assert.True(result.IsAccepted);
        Assert.Equal(state == "absent"
            ? ServiceOwnershipPriorDaclState.Absent
            : ServiceOwnershipPriorDaclState.Empty,
            result.Document!.Entries[0].PriorDaclState);
    }

    // ===========================================================================
    // ACTIVE IS NARROWLY REACHABLE (cycle 82, D1 - supersedes ruling B1.5)
    // ===========================================================================
    //
    // Until cycle 82 the parser refused EVERY active entry at an unconditional
    // fall-through brake, even one the exact approved profile authorised. Brian
    // authorised narrow acceptance, so the exact approved entry is now accepted and
    // ActiveLifecycleAcceptanceNotAuthorized is no longer produced by the parser.
    // Every near miss is still refused, by the SPECIFIC gate it failed - see
    // ServiceOwnershipActiveLedgerAcceptanceTests for the full near-miss matrix.

    [Fact]
    public void An_exact_approved_active_entry_is_accepted_and_the_terminal_brake_is_retired()
    {
        DocModel doc = Doc();
        doc.TransactionState = "done";
        doc.Entries[0].GrantedRightsMask = "00120009";
        doc.Entries[0].LifecycleState = "active";

        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(Build(doc));

        Assert.True(result.IsAccepted);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, result.Reason);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Active, result.Outcome);
        Assert.NotNull(result.Document);
        Assert.Equal(
            ServiceOwnershipLifecycleState.Active,
            result.Document!.Entries[0].LifecycleState);
    }

    [Fact]
    public void The_terminal_active_brake_reason_is_kept_in_the_vocabulary_but_never_produced()
    {
        // The member must NOT be deleted: ServiceOwnershipLedgerInvalidReason has no
        // explicit values, so removing it would silently renumber every later reason.
        Assert.Contains(
            "ActiveLifecycleAcceptanceNotAuthorized",
            Enum.GetNames(typeof(ServiceOwnershipLedgerInvalidReason)));

        // It still maps to a REFUSAL outcome, so a stray value can never be read as
        // acceptance.
        Assert.Equal(
            ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerContract.MapRefusalOutcome(
                ServiceOwnershipLedgerInvalidReason.ActiveLifecycleAcceptanceNotAuthorized));
    }

    // ---- cycle 38b: the active vocabulary is RETIRED, not merely unreachable ----
    //
    // These tests are deliberately REFLECTION-BASED. A compile-time reference to a
    // member that must not exist cannot express "must not exist" - it just stops
    // compiling. A source-text scan is not proof either, because a comment or a
    // string could satisfy or defeat it. Reflection over the LIVE vocabulary is the
    // only form that both compiles today and fails loudly if the member returns.

    [Fact]
    public void The_retired_active_vocabulary_is_absent_from_the_live_schema()
    {
        string[] outcomes = Enum.GetNames(typeof(ServiceOwnershipLedgerOutcome));
        string[] reasons = Enum.GetNames(typeof(ServiceOwnershipLedgerInvalidReason));

        Assert.DoesNotContain("ValidActive", outcomes);
        Assert.DoesNotContain("ActiveNotPermitted", reasons);

        // Cycle 82 APPENDED Active at the new slot 9. The retired slot-2 name stays
        // gone, so this is an addition at a fresh number, not a resurrection.
        Assert.Equal(
            new[]
            {
                "Absent", "ValidEmpty", "Malformed", "Unsupported", "Foreign",
                "Inconsistent", "InProgress", "Stale", "Active",
            },
            outcomes.OrderBy(n => (int)Enum.Parse<ServiceOwnershipLedgerOutcome>(n)).ToArray());

        Assert.Contains("NoApprovedRightsProfile", reasons);
        Assert.Contains("ForeignOwnership", reasons);

        // Cycle 42 appended SEVEN bounded reasons at the END, and cycle 48 appended
        // NINE more, so no existing value shifted. 52 + 7 + 9 = 68.
        Assert.Equal(68, reasons.Length);
        foreach (string added in new[]
                 {
                     "InvalidRightsProfileId", "ProviderProfileMismatch",
                     "BroadProviderClassificationNotApproved",
                     "ApprovedRightsProfileBitMissing",
                     "ApprovedRightsProfileBitNotPermitted",
                     "GenericReadRightNotAuthorized",
                     "ActiveLifecycleAcceptanceNotAuthorized",
                     "RetiredCngSecurityDescriptorMechanismUnsupported",
                     "InvalidProviderUniqueName",
                     "InvalidKeyStorageRoot",
                     "InvalidDescriptorFormat",
                     "PriorDescriptorMalformed",
                     "PriorDescriptorUnsupportedShape",
                     "PriorDescriptorContainsSacl",
                     "PriorDescriptorNotProtected",
                     "PriorDescriptorServiceAceAlreadyPresent",
                 })
        {
            Assert.Contains(added, reasons);
        }
    }

    [Fact]
    public void The_ledger_outcome_numbering_is_pinned_and_slot_two_is_permanently_retired()
    {
        // Pinned so a future REORDER is caught. The retired slot 2 is what
        // ValidActive occupied; reusing it would silently re-point persisted or
        // interop values at a different meaning.
        Assert.Equal(0, (int)ServiceOwnershipLedgerOutcome.Absent);
        Assert.Equal(1, (int)ServiceOwnershipLedgerOutcome.ValidEmpty);
        Assert.Equal(3, (int)ServiceOwnershipLedgerOutcome.Malformed);
        Assert.Equal(4, (int)ServiceOwnershipLedgerOutcome.Unsupported);
        Assert.Equal(5, (int)ServiceOwnershipLedgerOutcome.Foreign);
        Assert.Equal(6, (int)ServiceOwnershipLedgerOutcome.Inconsistent);
        Assert.Equal(7, (int)ServiceOwnershipLedgerOutcome.InProgress);
        Assert.Equal(8, (int)ServiceOwnershipLedgerOutcome.Stale);

        // Cycle 82 added Active at the next unused number, NOT at the retired slot 2.
        Assert.Equal(9, (int)ServiceOwnershipLedgerOutcome.Active);

        int[] declared = Enum.GetValues(typeof(ServiceOwnershipLedgerOutcome))
            .Cast<ServiceOwnershipLedgerOutcome>()
            .Select(v => (int)v)
            .OrderBy(v => v)
            .ToArray();

        Assert.Equal(new[] { 0, 1, 3, 4, 5, 6, 7, 8, 9 }, declared);
        Assert.DoesNotContain(2, declared);
        Assert.False(Enum.IsDefined(typeof(ServiceOwnershipLedgerOutcome), 2));
    }

    [Fact]
    public void No_refusal_reason_can_map_to_an_accepted_outcome()
    {
        foreach (ServiceOwnershipLedgerInvalidReason reason in
                 Enum.GetValues<ServiceOwnershipLedgerInvalidReason>())
        {
            ServiceOwnershipLedgerOutcome mapped = ServiceOwnershipLedgerContract.MapRefusalOutcome(reason);
            Assert.NotEqual(ServiceOwnershipLedgerOutcome.ValidEmpty, mapped);
            Assert.NotEqual(ServiceOwnershipLedgerOutcome.Absent, mapped);
            Assert.NotEqual(ServiceOwnershipLedgerOutcome.InProgress, mapped);
            Assert.NotEqual(ServiceOwnershipLedgerOutcome.Stale, mapped);
        }

        // The explicit absent factory is the ONLY producer of Absent, and it is not
        // an active record by any spelling.
        Assert.Equal(
            ServiceOwnershipLedgerOutcome.Absent,
            ServiceOwnershipLedgerValidator.ForAbsentLedger().Outcome);
    }

    /// <summary>
    /// Every rights mask a CNG entry can even REPRESENT: non-zero, inside the known
    /// vocabulary, and free of both the prohibited-management bits and the "not
    /// used" bit. Anything outside this set is refused earlier by mask validation,
    /// so it could never exercise the terminal active branch.
    /// </summary>
    public static IEnumerable<uint> RepresentableMasks()
    {
        uint allowed = ServiceOwnershipLedgerContract.KnownRightsBits
            & ~(ServiceOwnershipLedgerContract.ProhibitedManagementRightsBits
                | ServiceOwnershipLedgerContract.GenericExecuteRight);

        var bits = new List<uint>();
        for (int i = 0; i < 32; i++)
        {
            uint bit = 1u << i;
            if ((allowed & bit) != 0u)
            {
                bits.Add(bit);
            }
        }

        int combinations = 1 << bits.Count;
        for (int subset = 1; subset < combinations; subset++)
        {
            uint mask = 0u;
            for (int i = 0; i < bits.Count; i++)
            {
                if ((subset & (1 << i)) != 0)
                {
                    mask |= bits[i];
                }
            }
            yield return mask;
        }
    }

    /// <summary>
    /// Every FILE rights mask a v3 backing-file entry can even REPRESENT: non-zero,
    /// inside the known FILE vocabulary, and free of the FILE prohibited bits.
    /// GenericExecute is excluded explicitly even though it is also prohibited, to
    /// mirror the CryptoKeyRights generator's shape.
    /// </summary>
    public static IEnumerable<uint> RepresentableFileMasks()
    {
        uint allowed = ServiceOwnershipLedgerContract.FileKnownRightsBits
            & ~(ServiceOwnershipLedgerContract.FileProhibitedManagementRightsBits
                | ServiceOwnershipLedgerContract.FileGenericExecuteRight);

        var bits = new List<uint>();
        for (int i = 0; i < 32; i++)
        {
            uint bit = 1u << i;
            if ((allowed & bit) != 0u)
            {
                bits.Add(bit);
            }
        }

        int combinations = 1 << bits.Count;
        for (int subset = 1; subset < combinations; subset++)
        {
            uint mask = 0u;
            for (int i = 0; i < bits.Count; i++)
            {
                if ((subset & (1 << i)) != 0)
                {
                    mask |= bits[i];
                }
            }
            yield return mask;
        }
    }

    [Fact]
    public void A_raw_active_entry_is_refused_for_every_representable_mask_provider_and_mechanism_except_the_one_approved_combination()
    {
        // Raw JSON, lifecycleState "active", driven across EVERY representable FILE
        // mask and EVERY provider/mechanism pairing. Each case asserts the EXACT
        // bounded reason, so this is an enumeration and not a membership test.
        //
        // CYCLE 82. Exactly ONE of the 378 cases is now ACCEPTED - the exact approved
        // provider, mechanism and mask. It is the case that previously landed on the
        // terminal brake. Every other case refuses exactly as it did before, with the
        // same reason.
        uint[] masks = RepresentableFileMasks().ToArray();
        Assert.Equal(63, masks.Length);

        var pairings = new[]
        {
            (ProviderToken, MechanismToken),
            (ProviderToken, RetiredMechanismToken),
            (ProviderToken, "csp-key-file-dacl"),
            ("cng", MechanismToken),
            ("cng", RetiredMechanismToken),
            ("legacy-csp", "csp-key-file-dacl"),
        };

        uint approved = ServiceOwnershipLedgerContract.ApprovedRightsProfileMask;
        int cases = 0;
        int acceptedCases = 0;
        int genericReadCases = 0;
        int missingBitCases = 0;
        int notPermittedCases = 0;

        foreach (uint mask in masks)
        {
            foreach ((string provider, string mechanism) in pairings)
            {
                DocModel doc = Doc();
                doc.Entries[0].PrivateKeyProviderKind = provider;
                doc.Entries[0].GrantMechanism = mechanism;
                doc.Entries[0].GrantedRightsMask = mask.ToString("X8", CultureInfo.InvariantCulture);
                doc.Entries[0].LifecycleState = "active";

                ServiceOwnershipLedgerValidationResult result =
                    ServiceOwnershipLedgerValidator.Validate(Build(doc));

                Assert.DoesNotContain("ValidActive", Enum.GetNames(typeof(ServiceOwnershipLedgerOutcome)));

                ServiceOwnershipLedgerInvalidReason expected;
                if (provider == ProviderToken && mechanism == MechanismToken)
                {
                    if ((mask & ServiceOwnershipLedgerContract.GenericReadRight) != 0u)
                    {
                        expected = ServiceOwnershipLedgerInvalidReason.GenericReadRightNotAuthorized;
                        genericReadCases++;
                    }
                    else if ((mask & approved) != approved)
                    {
                        expected = ServiceOwnershipLedgerInvalidReason.ApprovedRightsProfileBitMissing;
                        missingBitCases++;
                    }
                    else if ((mask & ~approved) != 0u)
                    {
                        expected = ServiceOwnershipLedgerInvalidReason.ApprovedRightsProfileBitNotPermitted;
                        notPermittedCases++;
                    }
                    else
                    {
                        // THE ONE APPROVED COMBINATION. Accepted since cycle 82.
                        Assert.True(result.IsAccepted);
                        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, result.Reason);
                        Assert.NotNull(result.Document);
                        acceptedCases++;
                        cases++;
                        continue;
                    }
                }
                else if (mechanism == RetiredMechanismToken)
                {
                    expected = ServiceOwnershipLedgerInvalidReason.RetiredCngSecurityDescriptorMechanismUnsupported;
                }
                else if (provider == "legacy-csp" && mechanism == "csp-key-file-dacl")
                {
                    expected = ServiceOwnershipLedgerInvalidReason.UnsupportedProviderRightsVocabulary;
                }
                else
                {
                    expected = ServiceOwnershipLedgerInvalidReason.ProviderMechanismMismatch;
                }

                Assert.True(result.IsRefused);
                Assert.Null(result.Document);
                Assert.Equal(expected, result.Reason);
                cases++;
            }
        }

        Assert.Equal(63 * 6, cases);

        // EXACTLY ONE representable mask is accepted, and the other three exactness
        // refusals are all genuinely exercised.
        Assert.Equal(1, acceptedCases);
        Assert.Equal(32, genericReadCases);
        Assert.True(missingBitCases > 0);
        Assert.True(notPermittedCases > 0);
        Assert.Equal(63, acceptedCases + genericReadCases + missingBitCases + notPermittedCases);
    }

    [Fact]
    public void An_active_entry_is_accepted_under_every_transaction_and_prior_state_but_only_done_reports_active()
    {
        // CYCLE 82. This test previously asserted that all 15 combinations were
        // refused at the terminal brake. They are now ACCEPTED - the entry gates are
        // lifecycle-independent - but only the DONE transaction reports the Active
        // OUTCOME. Every other transaction stays InProgress, and the lifecycle
        // planner treats those pairs as incoherent.
        var priorStates = new[] { "absent", "empty", "present" };
        var transactions = new[] { "preparing", "credential-mutated", "ledger-committed", "restoring", "done" };
        int cases = 0;
        int activeOutcomes = 0;

        foreach (string prior in priorStates)
        {
            foreach (string transaction in transactions)
            {
                DocModel doc = Doc();
                doc.TransactionState = transaction;
                doc.Entries[0].GrantedRightsMask = "00120009";
                doc.Entries[0].LifecycleState = "active";
                doc.Entries[0].PriorDaclState = prior;
                if (prior != "present")
                {
                    doc.Entries[0].PriorDaclBytesBase64 = string.Empty;
                    doc.Entries[0].PriorDaclSha256 = string.Empty;
                }

                ServiceOwnershipLedgerValidationResult result =
                    ServiceOwnershipLedgerValidator.Validate(Build(doc));

                Assert.True(result.IsAccepted);
                Assert.NotNull(result.Document);

                if (transaction == "done")
                {
                    Assert.Equal(ServiceOwnershipLedgerOutcome.Active, result.Outcome);
                    activeOutcomes++;
                }
                else
                {
                    Assert.Equal(ServiceOwnershipLedgerOutcome.InProgress, result.Outcome);
                }

                cases++;
            }
        }

        Assert.Equal(15, cases);
        Assert.Equal(3, activeOutcomes);
    }

    [Fact]
    public void A_csp_entry_is_refused_with_the_unsupported_rights_vocabulary_reason()
    {
        foreach (string lifecycle in new[] { "intended", "active", "restoring", "restored", "stale" })
        {
            DocModel doc = Doc();
            doc.Entries[0].PrivateKeyProviderKind = "legacy-csp";
            doc.Entries[0].GrantMechanism = "csp-key-file-dacl";
            doc.Entries[0].LifecycleState = lifecycle;

            ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(Build(doc));

            Assert.True(result.IsRefused);
            Assert.Equal(
                ServiceOwnershipLedgerInvalidReason.UnsupportedProviderRightsVocabulary,
                result.Reason);
            Assert.Equal(ServiceOwnershipLedgerOutcome.Unsupported, result.Outcome);
        }
    }

    [Theory]
    [InlineData("cng", "csp-key-file-dacl")]
    [InlineData("legacy-csp", "microsoft-software-ksp-backing-file-dacl")]
    [InlineData(ProviderToken, "csp-key-file-dacl")]
    public void A_provider_mechanism_mismatch_is_refused(string provider, string mechanism)
    {
        DocModel doc = Doc();
        doc.Entries[0].PrivateKeyProviderKind = provider;
        doc.Entries[0].GrantMechanism = mechanism;

        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.ProviderMechanismMismatch);
    }

    [Fact]
    public void A_broad_cng_entry_is_refused_via_the_retired_mechanism_gate()
    {
        DocModel doc = Doc();
        doc.Entries[0].PrivateKeyProviderKind = "cng";
        doc.Entries[0].GrantMechanism = "cng-security-descriptor";

        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.RetiredCngSecurityDescriptorMechanismUnsupported);
    }

    // ===========================================================================
    // SID VALIDATION
    // ===========================================================================

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sid")]
    [InlineData("s-1-5-21-1-2-3-1001")]                     // lowercase S
    [InlineData("S-1-5-021-1-2-3")]                          // leading zero
    [InlineData("S-1-5")]                                    // no subauthority
    [InlineData("S-1-5-21-1-2-3-1001 ")]                     // trailing whitespace
    [InlineData(" S-1-5-21-1-2-3-1001")]                     // leading whitespace
    [InlineData("S-1-5-21-1-2-3-4294967296")]                // subauthority overflow
    [InlineData("S-1-5-21--1-2-3")]                          // empty component
    [InlineData("S-1-0x5-21-1-2-3")]                         // hexadecimal authority
    [InlineData("CONTOSO\\alice")]                           // localized account name
    public void A_malformed_owner_sid_is_refused(string sid)
    {
        Assert.False(ServiceOwnershipLedgerContract.IsCanonicalSidString(sid));

        DocModel doc = Doc();
        doc.Entries[0].OwningUserSid = sid;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidOwnerSid);
    }

    [Fact]
    public void A_sid_longer_than_the_bound_or_with_too_many_subauthorities_is_refused()
    {
        string tooLong = "S-1-5-" + string.Join("-", Enumerable.Repeat("4294967295", 20));
        Assert.True(tooLong.Length > ServiceOwnershipLedgerContract.MaxSidLength);
        Assert.False(ServiceOwnershipLedgerContract.IsCanonicalSidString(tooLong));

        string tooManySubs = "S-1-5-" + string.Join("-", Enumerable.Repeat("1", 16));
        Assert.True(tooManySubs.Length <= ServiceOwnershipLedgerContract.MaxSidLength);
        Assert.False(ServiceOwnershipLedgerContract.IsCanonicalSidString(tooManySubs));

        string atTheLimit = "S-1-5-" + string.Join("-", Enumerable.Repeat("1", 15));
        Assert.True(ServiceOwnershipLedgerContract.IsCanonicalSidString(atTheLimit));

        DocModel doc = Doc();
        doc.Entries[0].OwningUserSid = tooManySubs;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidOwnerSid);
    }

    [Theory]
    [InlineData("S-1-5-18")]        // LocalSystem
    [InlineData("S-1-5-19")]        // LocalService
    [InlineData("S-1-5-20")]        // NetworkService
    [InlineData("S-1-5-32-544")]    // BUILTIN\Administrators
    [InlineData("S-1-5-32-545")]    // BUILTIN\Users
    [InlineData("S-1-1-0")]         // Everyone
    [InlineData("S-1-5-11")]        // Authenticated Users
    [InlineData("S-1-5-80-1-2-3-4-5")] // a service virtual account is not a user
    [InlineData("S-1-5-21")]        // the domain prefix alone carries no RID
    public void A_non_user_owner_sid_is_refused(string sid)
    {
        Assert.False(ServiceOwnershipLedgerContract.IsUserOwnerSid(sid));

        DocModel doc = Doc();
        doc.Entries[0].OwningUserSid = sid;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidOwnerSid);
    }

    [Fact]
    public void A_real_user_shaped_sid_is_accepted_as_an_owner()
    {
        // POSITIVE CONTROL for the refusals above.
        Assert.True(ServiceOwnershipLedgerContract.IsUserOwnerSid(OwnerSid));
        Assert.True(ServiceOwnershipLedgerContract.IsCanonicalSidString(OwnerSid));
    }

    [Theory]
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-2002")] // a user, not a service
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-80")]        // no subauthority after 80
    [InlineData("S-1-5-80-0")]      // NT SERVICE\ALL SERVICES, not a specific service
    [InlineData("S-1-5-32-544")]
    [InlineData("not-a-sid")]
    public void A_non_service_service_sid_is_refused(string sid)
    {
        Assert.False(ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid(sid));

        DocModel doc = Doc();
        doc.Entries[0].ServiceSid = sid;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidServiceSid);
    }

    [Fact]
    public void A_service_virtual_account_sid_is_accepted_without_asserting_a_subauthority_count()
    {
        // POSITIVE CONTROL. Deliberately NOT asserting an exact subauthority count:
        // the count for a per-service virtual account SID is not documented on the
        // page this contract is grounded in, so asserting one would be an invention.
        Assert.True(ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid(SvcSid));
        Assert.True(ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid("S-1-5-80-1"));
        Assert.True(ServiceOwnershipLedgerContract.IsServiceVirtualAccountSid("S-1-5-80-1-2-3-4-5-6"));
    }

    [Fact]
    public void An_owner_sid_equal_to_the_service_sid_is_refused()
    {
        DocModel doc = Doc();
        doc.Entries[0].OwningUserSid = OwnerSid;
        doc.Entries[0].ServiceSid = OwnerSid;

        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.OwnerSidEqualsServiceSid);
    }

    // ===========================================================================
    // KEY IDENTITY GRAMMAR (binding correction 4)
    // ===========================================================================

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("C:")]
    [InlineData("C:\\keys\\k.pvk")]
    [InlineData("..")]
    [InlineData("a..b")]
    [InlineData("../secret")]
    [InlineData(".hidden")]
    [InlineData("%APPDATA%")]
    [InlineData("$env")]
    [InlineData("{id}")]
    [InlineData("a b")]
    [InlineData("\\\\server\\share")]
    public void An_illegal_key_identity_is_refused(string keyIdentity)
    {
        Assert.False(ServiceOwnershipLedgerContract.IsValidKeyIdentity(keyIdentity));

        DocModel doc = Doc();
        doc.Entries[0].KeyIdentity = keyIdentity;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidKeyIdentity);
    }

    [Fact]
    public void A_key_identity_longer_than_the_bound_is_refused()
    {
        string tooLong = new('a', ServiceOwnershipLedgerContract.MaxKeyIdentityLength + 1);
        Assert.False(ServiceOwnershipLedgerContract.IsValidKeyIdentity(tooLong));
        Assert.True(ServiceOwnershipLedgerContract.IsValidKeyIdentity(
            new string('a', ServiceOwnershipLedgerContract.MaxKeyIdentityLength)));

        DocModel doc = Doc();
        doc.Entries[0].KeyIdentity = tooLong;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidKeyIdentity);
    }

    [Fact]
    public void The_allow_listed_key_identity_grammar_is_accepted()
    {
        // POSITIVE CONTROL.
        Assert.True(ServiceOwnershipLedgerContract.IsValidKeyIdentity("A"));
        Assert.True(ServiceOwnershipLedgerContract.IsValidKeyIdentity("a.b_c-9"));
        Assert.True(ServiceOwnershipLedgerContract.IsValidKeyIdentity(KeyId));
    }

    // ===========================================================================
    // ENUM WIRE TOKENS - invalid and Unspecified for EVERY enum
    // ===========================================================================

    public static TheoryData<string, string, ServiceOwnershipLedgerInvalidReason> EnumFieldCases()
    {
        var data = new TheoryData<string, string, ServiceOwnershipLedgerInvalidReason>();
        (string Field, ServiceOwnershipLedgerInvalidReason Reason, string Valid)[] fields =
        {
            ("credentialKind", ServiceOwnershipLedgerInvalidReason.InvalidCredentialKind, "personal-app-registration-certificate"),
            ("provenance", ServiceOwnershipLedgerInvalidReason.InvalidProvenance, "referenced"),
            ("privateKeyProviderKind", ServiceOwnershipLedgerInvalidReason.InvalidProviderKind, "cng"),
            ("grantMechanism", ServiceOwnershipLedgerInvalidReason.InvalidGrantMechanism, "cng-security-descriptor"),
            ("priorDaclState", ServiceOwnershipLedgerInvalidReason.InvalidPriorDaclState, "present"),
            ("lifecycleState", ServiceOwnershipLedgerInvalidReason.InvalidLifecycleState, "intended"),
        };

        foreach ((string field, ServiceOwnershipLedgerInvalidReason reason, string valid) in fields)
        {
            foreach (string bad in new[]
                     {
                         "unspecified", "", "bogus", "0", "1",
                         valid.ToUpperInvariant(),
                         char.ToUpperInvariant(valid[0]) + valid[1..],
                         valid + " ",
                     })
            {
                data.Add(field, bad, reason);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EnumFieldCases))]
    public void Every_entry_enum_refuses_unspecified_unknown_and_wrong_case_tokens(
        string field, string badToken, ServiceOwnershipLedgerInvalidReason expected)
    {
        DocModel doc = Doc();
        EntryModel entry = doc.Entries[0];
        switch (field)
        {
            case "credentialKind": entry.CredentialKind = badToken; break;
            case "provenance": entry.Provenance = badToken; break;
            case "privateKeyProviderKind": entry.PrivateKeyProviderKind = badToken; break;
            case "grantMechanism": entry.GrantMechanism = badToken; break;
            case "priorDaclState": entry.PriorDaclState = badToken; break;
            case "lifecycleState": entry.LifecycleState = badToken; break;
            default: throw new InvalidOperationException("unmapped field");
        }

        Refused(Build(doc), expected);
    }

    [Theory]
    [InlineData("unspecified")]
    [InlineData("")]
    [InlineData("bogus")]
    [InlineData("IDLE")]
    [InlineData("Idle")]
    [InlineData("inventoryCommitted")]   // a ManagedChefKeys token, not a service token
    public void The_document_transaction_state_refuses_unspecified_unknown_and_wrong_case_tokens(string token)
    {
        DocModel doc = EmptyDoc();
        doc.TransactionState = token;

        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidTransactionState);
    }

    [Fact]
    public void Every_enum_zero_value_is_unspecified_and_has_no_wire_token()
    {
        Assert.Equal(0, (int)ServiceOwnershipCredentialKind.Unspecified);
        Assert.Equal(0, (int)ServiceOwnershipProvenance.Unspecified);
        Assert.Equal(0, (int)ServiceOwnershipPrivateKeyProviderKind.Unspecified);
        Assert.Equal(0, (int)ServiceOwnershipGrantMechanism.Unspecified);
        Assert.Equal(0, (int)ServiceOwnershipPriorDaclState.Unspecified);
        Assert.Equal(0, (int)ServiceOwnershipLifecycleState.Unspecified);
        Assert.Equal(0, (int)ServiceOwnershipTransactionState.Unspecified);

        Assert.Equal(string.Empty, ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipCredentialKind.Unspecified));
        Assert.Equal(string.Empty, ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipProvenance.Unspecified));
        Assert.Equal(string.Empty, ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipPrivateKeyProviderKind.Unspecified));
        Assert.Equal(string.Empty, ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipGrantMechanism.Unspecified));
        Assert.Equal(string.Empty, ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipPriorDaclState.Unspecified));
        Assert.Equal(string.Empty, ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipLifecycleState.Unspecified));
        Assert.Equal(string.Empty, ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipTransactionState.Unspecified));

        Assert.False(ServiceOwnershipLedgerContract.TryParseWireToken(string.Empty, out ServiceOwnershipCredentialKind _));
        Assert.False(ServiceOwnershipLedgerContract.TryParseWireToken(null, out ServiceOwnershipProvenance _));
        Assert.False(ServiceOwnershipLedgerContract.TryParseWireToken("unspecified", out ServiceOwnershipPrivateKeyProviderKind _));
        Assert.False(ServiceOwnershipLedgerContract.TryParseWireToken("unspecified", out ServiceOwnershipGrantMechanism _));
        Assert.False(ServiceOwnershipLedgerContract.TryParseWireToken("unspecified", out ServiceOwnershipPriorDaclState _));
        Assert.False(ServiceOwnershipLedgerContract.TryParseWireToken("unspecified", out ServiceOwnershipLifecycleState _));
        Assert.False(ServiceOwnershipLedgerContract.TryParseWireToken("unspecified", out ServiceOwnershipTransactionState _));
    }

    [Fact]
    public void Every_declared_enum_value_round_trips_through_its_wire_token()
    {
        AssertRoundTrip<ServiceOwnershipCredentialKind>(ServiceOwnershipLedgerContract.ToWireToken,
            (string? t, out ServiceOwnershipCredentialKind v) => ServiceOwnershipLedgerContract.TryParseWireToken(t, out v));
        AssertRoundTrip<ServiceOwnershipProvenance>(ServiceOwnershipLedgerContract.ToWireToken,
            (string? t, out ServiceOwnershipProvenance v) => ServiceOwnershipLedgerContract.TryParseWireToken(t, out v));
        AssertRoundTrip<ServiceOwnershipPrivateKeyProviderKind>(ServiceOwnershipLedgerContract.ToWireToken,
            (string? t, out ServiceOwnershipPrivateKeyProviderKind v) => ServiceOwnershipLedgerContract.TryParseWireToken(t, out v));
        AssertRoundTrip<ServiceOwnershipGrantMechanism>(ServiceOwnershipLedgerContract.ToWireToken,
            (string? t, out ServiceOwnershipGrantMechanism v) => ServiceOwnershipLedgerContract.TryParseWireToken(t, out v));
        AssertRoundTrip<ServiceOwnershipPriorDaclState>(ServiceOwnershipLedgerContract.ToWireToken,
            (string? t, out ServiceOwnershipPriorDaclState v) => ServiceOwnershipLedgerContract.TryParseWireToken(t, out v));
        AssertRoundTrip<ServiceOwnershipLifecycleState>(ServiceOwnershipLedgerContract.ToWireToken,
            (string? t, out ServiceOwnershipLifecycleState v) => ServiceOwnershipLedgerContract.TryParseWireToken(t, out v));
        AssertRoundTrip<ServiceOwnershipTransactionState>(ServiceOwnershipLedgerContract.ToWireToken,
            (string? t, out ServiceOwnershipTransactionState v) => ServiceOwnershipLedgerContract.TryParseWireToken(t, out v));
    }

    private delegate bool TryParse<T>(string? token, out T value);

    private static void AssertRoundTrip<T>(Func<T, string> toToken, TryParse<T> tryParse)
        where T : struct, Enum
    {
        foreach (T value in Enum.GetValues<T>())
        {
            string token = toToken(value);
            if (Convert.ToInt32(value, CultureInfo.InvariantCulture) == 0)
            {
                Assert.Equal(string.Empty, token);
                continue;
            }

            Assert.NotEqual(string.Empty, token);
            Assert.Equal(token.ToLowerInvariant(), token);
            Assert.True(tryParse(token, out T parsed));
            Assert.Equal(value, parsed);
        }
    }

    [Fact]
    public void The_wire_tokens_are_exactly_the_documented_lowercase_hyphen_strings()
    {
        Assert.Equal("personal-app-registration-certificate",
            ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipCredentialKind.PersonalAppRegistrationCertificate));
        Assert.Equal("referenced",
            ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipProvenance.Referenced));
        Assert.Equal("cng",
            ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipPrivateKeyProviderKind.Cng));
        Assert.Equal("legacy-csp",
            ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipPrivateKeyProviderKind.LegacyCsp));
        Assert.Equal("cng-security-descriptor",
            ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipGrantMechanism.CngSecurityDescriptor));
        Assert.Equal("csp-key-file-dacl",
            ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipGrantMechanism.CspKeyFileDacl));
        Assert.Equal("absent", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipPriorDaclState.Absent));
        Assert.Equal("empty", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipPriorDaclState.Empty));
        Assert.Equal("present", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipPriorDaclState.Present));
        Assert.Equal("intended", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipLifecycleState.Intended));
        Assert.Equal("active", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipLifecycleState.Active));
        Assert.Equal("restoring", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipLifecycleState.Restoring));
        Assert.Equal("restored", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipLifecycleState.Restored));
        Assert.Equal("stale", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipLifecycleState.Stale));
        Assert.Equal("foreign", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipLifecycleState.Foreign));
        Assert.Equal("idle", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipTransactionState.Idle));
        Assert.Equal("preparing", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipTransactionState.Preparing));
        Assert.Equal("credential-mutated", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipTransactionState.CredentialMutated));
        Assert.Equal("ledger-committed", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipTransactionState.LedgerCommitted));
        Assert.Equal("restoring", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipTransactionState.Restoring));
        Assert.Equal("done", ServiceOwnershipLedgerContract.ToWireToken(ServiceOwnershipTransactionState.Done));
    }

    // ===========================================================================
    // FOREIGN OWNERSHIP AND MARKERS
    // ===========================================================================

    [Fact]
    public void A_foreign_product_marker_is_refused_as_foreign()
    {
        DocModel doc = EmptyDoc();
        doc.ProductOwnershipMarker = ProvisioningContract.ProductOwnershipMarker;

        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(Build(doc));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.WrongMarker, result.Reason);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Foreign, result.Outcome);
    }

    [Fact]
    public void A_foreign_feature_id_is_refused_as_foreign()
    {
        DocModel doc = EmptyDoc();
        doc.ManagedFeatureId = ProvisioningContract.ManagedFeatureId;

        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(Build(doc));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.WrongFeatureId, result.Reason);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Foreign, result.Outcome);
    }

    [Fact]
    public void A_foreign_lifecycle_marker_fails_the_whole_ledger_closed()
    {
        DocModel doc = Doc();
        doc.Entries[0].LifecycleState = "foreign";

        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(Build(doc));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.ForeignOwnership, result.Reason);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Foreign, result.Outcome);
        Assert.Null(result.Document);
    }

    // ===========================================================================
    // PER-USER OWNERSHIP INVARIANTS
    // ===========================================================================

    [Fact]
    public void A_duplicate_entry_id_fails_the_whole_ledger()
    {
        DocModel doc = Doc();
        EntryModel second = Entry();
        second.EntryId = doc.Entries[0].EntryId;
        second.CertificateThumbprintSha1 = Thumb2;
        second.JobIds = new[] { "job-0002" };
        doc.Entries.Add(second);

        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.DuplicateEntryId);
    }

    [Fact]
    public void The_same_certificate_under_two_owners_is_a_cross_owner_collision()
    {
        DocModel doc = Doc();
        EntryModel second = Entry();
        second.EntryId = "entry-0002";
        second.OwningUserSid = OwnerSid2;
        second.JobIds = new[] { "job-0002" };
        doc.Entries.Add(second);

        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.CrossOwnerCertificateCollision);
    }

    [Fact]
    public void The_same_job_under_two_owners_is_a_cross_owner_collision()
    {
        DocModel doc = Doc();
        EntryModel second = Entry();
        second.EntryId = "entry-0002";
        second.OwningUserSid = OwnerSid2;
        second.CertificateThumbprintSha1 = Thumb2;
        second.JobIds = new[] { "job-0001" };
        doc.Entries.Add(second);

        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.CrossOwnerJobCollision);
    }

    [Fact]
    public void The_same_job_twice_under_one_owner_is_a_duplicate()
    {
        DocModel doc = Doc();
        doc.Entries[0].JobIds = new[] { "job-0001", "job-0001" };

        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.DuplicateJobId);
    }

    [Fact]
    public void The_same_job_in_two_entries_of_one_owner_is_a_duplicate()
    {
        DocModel doc = Doc();
        EntryModel second = Entry();
        second.EntryId = "entry-0002";
        second.CertificateThumbprintSha1 = Thumb2;
        second.JobIds = new[] { "job-0001" };
        doc.Entries.Add(second);

        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.DuplicateJobId);
    }

    [Fact]
    public void Entries_sharing_a_certificate_must_agree_on_every_bound_field()
    {
        (string Name, Action<EntryModel> Mutate)[] disagreements =
        {
            ("serviceSid", e => e.ServiceSid = "S-1-5-80-9-9-9-9-9"),
            ("keyIdentity", e => e.KeyIdentity = "a-different-key-identity"),
            ("grantedRightsMask", e => e.GrantedRightsMask = "00000001"),
            ("priorDaclState", e =>
            {
                e.PriorDaclState = "absent";
                e.PriorDaclBytesBase64 = string.Empty;
                e.PriorDaclSha256 = string.Empty;
            }),
        };

        foreach ((string name, Action<EntryModel> mutate) in disagreements)
        {
            DocModel doc = Doc();
            EntryModel second = Entry();
            second.EntryId = "entry-0002";
            second.JobIds = new[] { "job-0002" };
            mutate(second);
            doc.Entries.Add(second);

            ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(Build(doc));
            Assert.True(result.IsRefused, name);
            Assert.Equal(
                ServiceOwnershipLedgerInvalidReason.InconsistentSharedCertificateFields,
                result.Reason);
        }
    }

    [Fact]
    public void Entries_sharing_a_certificate_and_agreeing_on_every_bound_field_are_accepted()
    {
        // POSITIVE CONTROL for the disagreement test above.
        DocModel doc = Doc();
        EntryModel second = Entry();
        second.EntryId = "entry-0002";
        second.JobIds = new[] { "job-0002" };
        doc.Entries.Add(second);

        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(Build(doc));
        Assert.True(result.IsAccepted);
        Assert.Equal(2, result.Document!.Entries.Count);
    }

    [Fact]
    public void A_mismatched_rights_policy_version_is_refused()
    {
        // Deliberately NOT using "the next version number": policy version 1 is the
        // PERMANENTLY retired predecessor, and 0 / a huge value can never be valid.
        foreach (string raw in new[] { "1", "0", "2147483647" })
        {
            DocModel doc = Doc();
            doc.Entries[0].RightsPolicyVersionRaw = raw;
            Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidRightsPolicyVersion);
        }
    }

    // ===========================================================================
    // PRIOR-DACL RESTORATION
    // ===========================================================================

    [Theory]
    [InlineData("absent")]
    [InlineData("empty")]
    public void A_prior_dacl_payload_is_not_allowed_when_no_prior_dacl_existed(string state)
    {
        DocModel doc = Doc();
        doc.Entries[0].PriorDaclState = state;
        doc.Entries[0].PriorDaclBytesBase64 = PriorB64;
        doc.Entries[0].PriorDaclSha256 = string.Empty;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.PriorDaclPayloadNotAllowed);

        doc = Doc();
        doc.Entries[0].PriorDaclState = state;
        doc.Entries[0].PriorDaclBytesBase64 = string.Empty;
        doc.Entries[0].PriorDaclSha256 = PriorSha;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.PriorDaclPayloadNotAllowed);
    }

    [Fact]
    public void A_present_prior_dacl_requires_both_payload_fields()
    {
        DocModel doc = Doc();
        doc.Entries[0].PriorDaclBytesBase64 = string.Empty;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.PriorDaclPayloadMissing);

        doc = Doc();
        doc.Entries[0].PriorDaclSha256 = string.Empty;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.PriorDaclPayloadMissing);
    }

    [Theory]
    [InlineData("AQIDB")]        // length not a multiple of 4
    [InlineData("AQID BA==")]    // embedded whitespace
    [InlineData("AQ=IDBA=")]     // padding in the middle
    [InlineData("AQIDB*==")]     // illegal character
    [InlineData("====")]         // padding only
    public void An_invalid_base64_prior_dacl_payload_is_refused(string payload)
    {
        DocModel doc = Doc();
        doc.Entries[0].PriorDaclBytesBase64 = payload;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidBase64);
    }

    [Fact]
    public void An_oversized_prior_dacl_payload_is_refused()
    {
        byte[] tooBig = new byte[ServiceOwnershipLedgerContract.MaxPriorDaclDecodedBytes + 1];
        string encoded = Convert.ToBase64String(tooBig);
        Assert.True(encoded.Length <= ServiceOwnershipLedgerContract.MaxPriorDaclBase64Length);

        DocModel doc = Doc();
        doc.Entries[0].PriorDaclBytesBase64 = encoded;
        doc.Entries[0].PriorDaclSha256 = Hex(SHA256.HashData(tooBig));
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.OversizedPriorDacl);

        byte[] wayTooBig = new byte[70000];
        string wayEncoded = Convert.ToBase64String(wayTooBig);
        Assert.True(wayEncoded.Length > ServiceOwnershipLedgerContract.MaxPriorDaclBase64Length);

        doc = Doc();
        doc.Entries[0].PriorDaclBytesBase64 = wayEncoded;
        doc.Entries[0].PriorDaclSha256 = Hex(SHA256.HashData(wayTooBig));
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.OversizedPriorDacl);
    }

    [Fact]
    public void A_prior_dacl_hash_that_does_not_match_the_decoded_bytes_is_refused()
    {
        DocModel doc = Doc();
        doc.Entries[0].PriorDaclSha256 = new string('B', 64);
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.PriorDaclHashMismatch);

        doc = Doc();
        doc.Entries[0].PriorDaclSha256 = PriorSha.ToLowerInvariant();
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.PriorDaclHashMismatch);

        doc = Doc();
        doc.Entries[0].PriorDaclSha256 = "NOTHEX";
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.PriorDaclHashMismatch);
    }

    [Fact]
    public void A_captured_state_binding_that_does_not_match_is_refused()
    {
        DocModel doc = Doc();
        doc.Entries[0].CapturedStateBindingOverride = new string('C', 64);
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.CapturedStateBindingMismatch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nothex")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789")] // lowercase
    [InlineData("F4CC1F6EE4267A2028CBC55F473C2BA9523DED1A748756E63412B260F25D05")]   // short
    public void A_malformed_captured_state_binding_is_refused(string binding)
    {
        DocModel doc = Doc();
        doc.Entries[0].CapturedStateBindingOverride = binding;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidCapturedStateBinding);
    }

    [Fact]
    public void The_binding_is_sensitive_to_every_field_it_covers()
    {
        // Each mutation changes the preimage, so a binding computed over the ORIGINAL
        // field values must stop matching. This proves the binding is load-bearing.
        (string Name, Action<DocModel> Mutate)[] mutations =
        {
            ("generation", d => d.GenerationRaw = "2"),
            ("owningUserSid", d => d.Entries[0].OwningUserSid = OwnerSid2),
            ("serviceSid", d => d.Entries[0].ServiceSid = "S-1-5-80-9-9-9-9-9"),
            ("certificateThumbprintSha1", d => d.Entries[0].CertificateThumbprintSha1 = Thumb2),
            ("keyIdentity", d => d.Entries[0].KeyIdentity = "another-identity"),
            ("grantedRightsMask", d => d.Entries[0].GrantedRightsMask = "00000001"),
        };

        string baseline = ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            1, OwnerSid, SvcSid, Thumb,
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId, KeyId,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl, Mask,
            ServiceOwnershipPriorDaclState.Present, PriorSha,
            ProviderUniqueNameValue, ServiceOwnershipKeyStorageRoot.MicrosoftSoftwareKeyStorageProviderMachineKeys,
            ServiceOwnershipDescriptorFormat.MicrosoftSoftwareKspBackingFileSelfRelativeV1);

        foreach ((string name, Action<DocModel> mutate) in mutations)
        {
            DocModel doc = Doc();
            mutate(doc);
            doc.Entries[0].CapturedStateBindingOverride = baseline;

            ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(Build(doc));
            Assert.True(result.IsRefused, name);
            Assert.Equal(
                ServiceOwnershipLedgerInvalidReason.CapturedStateBindingMismatch,
                result.Reason);
        }
    }

    // ===========================================================================
    // STRICT PARSER
    // ===========================================================================

    [Fact]
    public void An_oversized_document_is_refused_before_it_is_parsed()
    {
        string oversized = new('a', ServiceOwnershipLedgerContract.MaxLedgerBytes + 1);
        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(oversized);

        Assert.Equal(ServiceOwnershipLedgerInvalidReason.OversizedInput, result.Reason);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Malformed, result.Outcome);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    public void A_root_that_is_not_an_object_is_malformed(string json)
    {
        Refused(json, ServiceOwnershipLedgerInvalidReason.MalformedJson);
    }

    [Fact]
    public void A_duplicate_root_property_is_refused()
    {
        string json = Build(EmptyDoc());
        string doubled = json[..^1] + ",\"schemaVersion\":1}";
        Refused(doubled, ServiceOwnershipLedgerInvalidReason.DuplicateProperty);
    }

    [Fact]
    public void A_duplicate_entry_property_is_refused()
    {
        DocModel doc = Doc();
        doc.Entries[0].ExtraJson = ",\"entryId\":\"entry-0001\"";
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.DuplicateProperty);
    }

    [Fact]
    public void An_unknown_root_property_is_refused()
    {
        DocModel doc = EmptyDoc();
        doc.ExtraJson = ",\"somethingNew\":1";
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.UnknownProperty);
    }

    [Fact]
    public void An_unknown_entry_property_is_refused()
    {
        DocModel doc = Doc();
        doc.Entries[0].ExtraJson = ",\"somethingNew\":1";
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.UnknownProperty);
    }

    public static TheoryData<string> ProhibitedNames()
    {
        var data = new TheoryData<string>();
        foreach (string name in new[]
                 {
                     "clientSecret", "secret", "password", "token", "claim", "claims",
                     "tenantId", "clientId", "upn", "account", "privateKey",
                     "privateKeyBytes", "pfx", "certificate", "certificateBytes", "path",
                     "filePath", "registryPath", "command", "arguments", "environment",
                     "subject", "issuer", "serialNumber", "message", "error", "exception",
                     "stackTrace",
                 })
        {
            data.Add(name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ProhibitedNames))]
    public void Every_prohibited_root_property_name_fails_the_whole_document(string name)
    {
        Assert.Contains(name, ServiceOwnershipLedgerContract.ProhibitedPropertyNames);

        foreach (string value in new[] { "null", "\"\"", "\"x\"", "0" })
        {
            DocModel doc = EmptyDoc();
            doc.ExtraJson = ",\"" + name + "\":" + value;
            Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.ProhibitedProperty);
        }

        // Case-insensitive: an upper-cased alias is refused just the same.
        DocModel upper = EmptyDoc();
        upper.ExtraJson = ",\"" + name.ToUpperInvariant() + "\":null";
        Refused(Build(upper), ServiceOwnershipLedgerInvalidReason.ProhibitedProperty);
    }

    [Theory]
    [MemberData(nameof(ProhibitedNames))]
    public void Every_prohibited_entry_property_name_fails_the_whole_document(string name)
    {
        DocModel doc = Doc();
        doc.Entries[0].ExtraJson = ",\"" + name + "\":null";
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.ProhibitedProperty);
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("productOwnershipMarker")]
    [InlineData("managedFeatureId")]
    [InlineData("installationOwnershipId")]
    [InlineData("generation")]
    [InlineData("transactionState")]
    [InlineData("entries")]
    [InlineData("createdUtc")]
    [InlineData("updatedUtc")]
    [InlineData("lastOperationId")]
    public void A_missing_root_property_is_refused(string omit)
    {
        DocModel doc = EmptyDoc();
        doc.Omit = omit;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.MissingProperty);
    }

    [Theory]
    [InlineData("entryId")]
    [InlineData("owningUserSid")]
    [InlineData("serviceSid")]
    [InlineData("credentialKind")]
    [InlineData("certificateThumbprintSha1")]
    [InlineData("provenance")]
    [InlineData("privateKeyProviderKind")]
    [InlineData("rightsProfileId")]
    [InlineData("keyIdentity")]
    [InlineData("grantMechanism")]
    [InlineData("grantedRightsMask")]
    [InlineData("rightsPolicyVersion")]
    [InlineData("priorDaclState")]
    [InlineData("priorDaclBytesBase64")]
    [InlineData("priorDaclSha256")]
    [InlineData("capturedStateBindingSha256")]
    [InlineData("associatedPromotedJobIds")]
    [InlineData("lifecycleState")]
    [InlineData("createdUtc")]
    [InlineData("updatedUtc")]
    [InlineData("providerUniqueName")]
    [InlineData("keyStorageRoot")]
    [InlineData("descriptorFormat")]
    public void A_missing_entry_property_is_refused(string omit)
    {
        DocModel doc = Doc();
        doc.Entries[0].Omit = omit;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.MissingProperty);
    }

    [Theory]
    [InlineData("\"1\"")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("[]")]
    [InlineData("99999999999999999999")]
    public void A_non_strict_integer_schema_version_is_the_wrong_type(string raw)
    {
        DocModel doc = EmptyDoc();
        doc.SchemaVersionRaw = raw;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.WrongType);
    }

    [Theory]
    [InlineData("+1")]   // JSON forbids a leading plus
    [InlineData("01")]   // JSON forbids a leading zero
    [InlineData(".5")]
    public void A_number_the_json_grammar_itself_forbids_is_malformed(string raw)
    {
        DocModel doc = EmptyDoc();
        doc.SchemaVersionRaw = raw;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.MalformedJson);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483647")]
    public void An_unsupported_schema_version_is_refused(string raw)
    {
        // Deliberately NOT using "the next version number": a fixture built on a
        // version that may later be supported silently becomes a false fixture.
        // Schema v1 is covered separately, because it is PERMANENTLY retired.
        DocModel doc = EmptyDoc();
        doc.SchemaVersionRaw = raw;

        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(Build(doc));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.UnsupportedSchemaVersion, result.Reason);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Unsupported, result.Outcome);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void An_invalid_generation_is_refused(string raw)
    {
        DocModel doc = EmptyDoc();
        doc.GenerationRaw = raw;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidGeneration);
    }

    [Theory]
    [InlineData("\"2026-08-06 00:00:00Z\"")]
    [InlineData("\"2026-08-06T00:00:00\"")]
    [InlineData("\"2026-08-06T00:00:00+00:00\"")]
    [InlineData("\"2026-08-06T00:00:00.000Z\"")]
    [InlineData("\"2026-13-06T00:00:00Z\"")]
    [InlineData("\"\"")]
    public void A_malformed_timestamp_is_refused(string raw)
    {
        DocModel doc = EmptyDoc();
        doc.CreatedUtcRaw = raw;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidTimestamp);
    }

    [Fact]
    public void A_created_timestamp_after_the_updated_timestamp_is_refused()
    {
        DocModel doc = EmptyDoc();
        doc.CreatedUtcRaw = "\"" + LaterStamp + "\"";
        doc.UpdatedUtcRaw = "\"" + Stamp + "\"";
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidTimestamp);

        DocModel entryDoc = Doc();
        entryDoc.Entries[0].CreatedUtc = LaterStamp;
        entryDoc.Entries[0].UpdatedUtc = Stamp;
        Refused(Build(entryDoc), ServiceOwnershipLedgerInvalidReason.InvalidTimestamp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("has:colon")]
    public void A_malformed_last_operation_id_is_refused(string id)
    {
        DocModel doc = EmptyDoc();
        doc.LastOperationId = id;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidOperationId);
    }

    [Fact]
    public void A_last_operation_id_longer_than_the_bound_is_refused()
    {
        DocModel doc = EmptyDoc();
        doc.LastOperationId = new string('a', ServiceOwnershipLedgerContract.MaxStringLength + 1);
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidOperationId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has\\backslash")]
    public void A_malformed_installation_ownership_id_is_refused(string id)
    {
        DocModel doc = EmptyDoc();
        doc.InstallationOwnershipId = id;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidInstallationOwnershipId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    public void A_malformed_entry_id_is_refused(string id)
    {
        DocModel doc = Doc();
        doc.Entries[0].EntryId = id;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidEntryId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    public void A_malformed_job_id_is_refused(string id)
    {
        DocModel doc = Doc();
        doc.Entries[0].JobIds = new[] { id };
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidJobId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef01")]   // lowercase
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0")]    // 39 characters
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF012")]  // 41 characters
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEFGH")]   // non-hex
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF01ABCDEF0123456789ABCDEF0123456789ABCDEF01")] // a SHA-256, not a SHA-1
    public void A_malformed_certificate_thumbprint_is_refused(string thumbprint)
    {
        DocModel doc = Doc();
        doc.Entries[0].CertificateThumbprintSha1 = thumbprint;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidThumbprint);
    }

    [Fact]
    public void Too_many_entries_are_refused()
    {
        DocModel doc = EmptyDoc();
        doc.TransactionState = "preparing";
        for (int i = 0; i < ServiceOwnershipLedgerContract.MaxEntries + 1; i++)
        {
            EntryModel e = Entry();
            e.EntryId = "entry-" + i.ToString("D4", CultureInfo.InvariantCulture);
            e.CertificateThumbprintSha1 = i.ToString("D4", CultureInfo.InvariantCulture) + Thumb[4..];
            e.JobIds = new[] { "job-" + i.ToString("D4", CultureInfo.InvariantCulture) };
            doc.Entries.Add(e);
        }

        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.TooManyEntries);
    }

    [Fact]
    public void Too_many_job_ids_are_refused()
    {
        DocModel doc = Doc();
        doc.Entries[0].JobIds = Enumerable
            .Range(0, ServiceOwnershipLedgerContract.MaxJobIdsPerEntry + 1)
            .Select(i => "job-" + i.ToString("D4", CultureInfo.InvariantCulture))
            .ToArray();

        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.TooManyJobIds);
    }

    [Fact]
    public void An_empty_ledger_may_not_claim_an_in_flight_transaction()
    {
        foreach (string state in new[] { "preparing", "credential-mutated", "ledger-committed", "restoring" })
        {
            DocModel doc = EmptyDoc();
            doc.TransactionState = state;
            Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidTransactionState);
        }

        // POSITIVE CONTROL: the two quiescent states ARE accepted with no entries.
        foreach (string state in new[] { "idle", "done" })
        {
            DocModel doc = EmptyDoc();
            doc.TransactionState = state;
            Assert.True(ServiceOwnershipLedgerValidator.Validate(Build(doc)).IsAccepted, state);
        }
    }

    [Fact]
    public void A_non_empty_ledger_may_not_claim_to_be_idle()
    {
        DocModel doc = Doc();
        doc.TransactionState = "idle";
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidTransactionState);
    }

    [Fact]
    public void Entries_must_be_an_array()
    {
        DocModel doc = EmptyDoc();
        doc.EntriesRaw = "{}";
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.WrongType);

        doc = EmptyDoc();
        doc.EntriesRaw = "null";
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.WrongType);
    }

    [Fact]
    public void An_entry_that_is_not_an_object_is_the_wrong_type()
    {
        DocModel doc = EmptyDoc();
        doc.TransactionState = "preparing";
        doc.EntriesRaw = "[1]";
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.WrongType);
    }

    [Fact]
    public void A_job_id_list_that_is_not_an_array_of_strings_is_the_wrong_type()
    {
        DocModel doc = Doc();
        doc.Entries[0].JobIdsRaw = "{}";
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.WrongType);

        doc = Doc();
        doc.Entries[0].JobIdsRaw = "[1]";
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.WrongType);
    }

    // ===========================================================================
    // NO EXCEPTION EVER ESCAPES
    // ===========================================================================

    // Deliberately a Fact rather than a Theory: several of these inputs contain lone
    // surrogates, a NUL and a BOM, which are hostile to test-case serialisation and
    // would be silently mangled before ever reaching the validator.
    private static string[] FuzzCases() => new[]
    {
        "{",
        "{\"schemaVersion\":",
        "{\"schemaVersion\":1,}",
        "[[[[[[[[[[",
        new string('[', 512) + new string(']', 512),
        "{\"entries\":" + new string('[', 200) + new string(']', 200) + "}",
        "{\"generation\":1" + new string('0', 400) + "}",
        "{\"generation\":-1e999999}",
        "{\"a\":\"\ud800\"}",                    // lone high surrogate
        "\ud800\udc00\ud800",                    // trailing lone surrogate
        "{\"a\":\"\0\"}",                        // embedded NUL
        "\0",
        "{\"schemaVersion\":1,\"entries\":[{\"entryId\":\"\ud800\"}]}",
        "\ufeff{\"schemaVersion\":1}",           // BOM prefixed
        "{\"schemaVersion\":1e309}",
    };

    [Fact]
    public void No_input_can_make_the_validator_throw()
    {
        string[] cases = FuzzCases();
        Assert.Equal(15, cases.Length);

        for (int i = 0; i < cases.Length; i++)
        {
            ServiceOwnershipLedgerValidationResult result =
                ServiceOwnershipLedgerValidator.Validate(cases[i]);

            Assert.True(result.IsRefused, "case " + i);
            Assert.NotEqual(ServiceOwnershipLedgerInvalidReason.None, result.Reason);
            Assert.Null(result.Document);
            Assert.True(Enum.IsDefined(result.Reason));
            Assert.True(Enum.IsDefined(result.Outcome));
        }
    }

    [Fact]
    public void Every_invalid_reason_maps_to_a_refused_outcome()
    {
        foreach (ServiceOwnershipLedgerInvalidReason reason in
                 Enum.GetValues<ServiceOwnershipLedgerInvalidReason>())
        {
            ServiceOwnershipLedgerOutcome outcome = ServiceOwnershipLedgerContract.MapRefusalOutcome(reason);
            Assert.Contains(outcome, new[]
            {
                ServiceOwnershipLedgerOutcome.Malformed,
                ServiceOwnershipLedgerOutcome.Unsupported,
                ServiceOwnershipLedgerOutcome.Foreign,
                ServiceOwnershipLedgerOutcome.Inconsistent,
            });
        }
    }

    // ===========================================================================
    // IMMUTABLE RETURNED COLLECTIONS
    // ===========================================================================

    [Fact]
    public void Returned_collections_are_genuinely_read_only()
    {
        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(Build(Doc()));
        Assert.True(result.IsAccepted);

        AssertReadOnly(result.Document!.Entries, result.Document.Entries[0]);
        AssertReadOnlyStrings(result.Document.Entries[0].AssociatedPromotedJobIds);
        AssertReadOnlyStrings(ServiceOwnershipLedgerContract.DocumentPropertyNames);
        AssertReadOnlyStrings(ServiceOwnershipLedgerContract.EntryPropertyNames);
        AssertReadOnlyStrings(ServiceOwnershipLedgerContract.ProhibitedPropertyNames);
        AssertReadOnlyStrings(ServiceOwnershipLedgerContract.BindingFieldOrder);

        IList<uint> bits = (IList<uint>)ServiceOwnershipLedgerContract.SpecificRightsBits;
        Assert.True(bits.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => bits.Add(1u));
    }

    private static void AssertReadOnly(
        IReadOnlyList<ServiceOwnershipLedgerEntry> collection,
        ServiceOwnershipLedgerEntry sample)
    {
        IList<ServiceOwnershipLedgerEntry> list = (IList<ServiceOwnershipLedgerEntry>)collection;
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add(sample));
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    private static void AssertReadOnlyStrings(IReadOnlyList<string> collection)
    {
        IList<string> list = (IList<string>)collection;
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Add("x"));
        Assert.Throws<NotSupportedException>(() => list.Clear());
    }

    // ===========================================================================
    // STRUCTURAL CONTAINMENT - every negative scan has a positive control
    // ===========================================================================

    private const string ContractRelativePath =
        "src/PAXCookbook.Shared/Contracts/ServiceOwnershipLedgerContract.cs";

    public static TheoryData<string> BannedApiTokens() => new()
    {
        "X509", "SecurityIdentifier", "RegistryKey", "File.", "Directory.", "Process",
        "HttpClient", "Environment.GetFolderPath", "AccessControl",
    };

    [Theory]
    [MemberData(nameof(BannedApiTokens))]
    public void The_contract_source_contains_no_banned_capability_token(string token)
    {
        string source = StripComments(File.ReadAllText(ContractPath()));
        Assert.DoesNotContain(token, source, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BannedApiTokens))]
    public void The_banned_token_scanner_fires_on_a_synthetic_positive_control(string token)
    {
        // POSITIVE CONTROL. Without this, "0 offenders" would be unfalsifiable: a
        // broken scanner and a clean file look identical.
        string synthetic = "var x = 1; /* not a comment marker */ " + token + " y = 2;";
        Assert.Contains(token, StripComments(synthetic), StringComparison.Ordinal);
    }

    [Fact]
    public void The_contract_source_imports_only_portable_framework_namespaces()
    {
        // Only the file-scope directives, i.e. everything before the namespace
        // declaration. A "using" STATEMENT inside a method body is not an import.
        var usings = new List<string>();
        foreach (string rawLine in StripComments(File.ReadAllText(ContractPath())).Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("namespace ", StringComparison.Ordinal))
            {
                break;
            }
            if (line.StartsWith("using ", StringComparison.Ordinal))
            {
                usings.Add(line);
            }
        }

        Assert.NotEmpty(usings);
        foreach (string line in usings)
        {
            Assert.Contains(line, new[]
            {
                "using System;",
                "using System.Collections.Generic;",
                "using System.Globalization;",
                "using System.Security.Cryptography;",
                "using System.Text;",
                "using System.Text.Json;",
            });
        }
    }

    [Fact]
    public void The_comment_stripper_removes_both_comment_forms_and_leaves_code()
    {
        // POSITIVE CONTROL for every "comment-stripped" claim in this file.
        const string sample = "int a = 1; // X509Store\n/* HttpClient */ int b = 2;";
        string stripped = StripComments(sample);

        Assert.DoesNotContain("X509", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", stripped, StringComparison.Ordinal);
        Assert.Contains("int a = 1;", stripped, StringComparison.Ordinal);
        Assert.Contains("int b = 2;", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void The_contract_source_declares_the_coincidental_homonym_in_plain_words()
    {
        // The comment is the durable record for the next reader, so it is asserted
        // on the RAW source (comments intact) rather than the stripped copy.
        string raw = File.ReadAllText(ContractPath());
        Assert.Contains("coincidental homonym", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ProvisioningContract.LedgerFileName", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("single-sourced", raw, StringComparison.OrdinalIgnoreCase);
    }

    private static string ContractPath()
    {
        string path = Path.Combine(RepoRoot(), ContractRelativePath.Replace('/', Path.DirectorySeparatorChar));
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

    // ===========================================================================
    // FIXTURE BUILDERS
    // ===========================================================================

    private static void Refused(string? json, ServiceOwnershipLedgerInvalidReason expected)
    {
        ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(json);
        Assert.True(result.IsRefused);
        Assert.Equal(expected, result.Reason);
        Assert.Null(result.Document);
        Assert.Equal(ServiceOwnershipLedgerContract.MapRefusalOutcome(expected), result.Outcome);
    }

    private static string ValidEmptyDocumentJson() => Build(EmptyDoc());

    private static DocModel EmptyDoc() => new();

    private static DocModel Doc()
    {
        DocModel doc = new() { TransactionState = "preparing" };
        doc.Entries.Add(Entry());
        return doc;
    }

    private static EntryModel Entry() => new();

    private sealed class DocModel
    {
        public string SchemaVersionRaw = "3";
        public string ProductOwnershipMarker = ServiceOwnershipLedgerContract.ProductOwnershipMarker;
        public string ManagedFeatureId = ServiceOwnershipLedgerContract.ManagedFeatureId;
        public string InstallationOwnershipId = "install-0001";
        public string GenerationRaw = "1";
        public string TransactionState = "idle";
        public string? EntriesRaw;
        public string CreatedUtcRaw = "\"" + Stamp + "\"";
        public string UpdatedUtcRaw = "\"" + Stamp + "\"";
        public string LastOperationId = "op-0001";
        public string ExtraJson = string.Empty;
        public string? Omit;
        public List<EntryModel> Entries = new();
    }

    private sealed class EntryModel
    {
        public string EntryId = "entry-0001";
        public string OwningUserSid = OwnerSid;
        public string ServiceSid = SvcSid;
        public string CredentialKind = "personal-app-registration-certificate";
        public string CertificateThumbprintSha1 = Thumb;
        public string Provenance = "referenced";
        public string PrivateKeyProviderKind = ProviderToken;
        public string RightsProfileId = ProfileToken;
        public string KeyIdentity = KeyId;
        public string GrantMechanism = MechanismToken;
        public string GrantedRightsMask = Mask;
        public string RightsPolicyVersionRaw = "3";
        public string PriorDaclState = "present";
        public string PriorDaclBytesBase64 = PriorB64;
        public string PriorDaclSha256 = PriorSha;
        public string? CapturedStateBindingOverride;
        public string[] JobIds = { "job-0001" };
        public string? JobIdsRaw;
        public string LifecycleState = "intended";
        public string CreatedUtc = Stamp;
        public string UpdatedUtc = Stamp;
        public string ProviderUniqueName = ProviderUniqueNameValue;
        public string KeyStorageRoot = KeyStorageRootToken;
        public string DescriptorFormat = DescriptorFormatToken;
        public string ExtraJson = string.Empty;
        public string? Omit;
    }

    private static string Q(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal)
                    .Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal)
                    .Replace("\t", "\\t", StringComparison.Ordinal) + "\"";

    private static string Build(DocModel doc)
    {
        var parts = new List<(string Name, string Raw)>
        {
            ("schemaVersion", doc.SchemaVersionRaw),
            ("productOwnershipMarker", Q(doc.ProductOwnershipMarker)),
            ("managedFeatureId", Q(doc.ManagedFeatureId)),
            ("installationOwnershipId", Q(doc.InstallationOwnershipId)),
            ("generation", doc.GenerationRaw),
            ("transactionState", Q(doc.TransactionState)),
            ("entries", doc.EntriesRaw ?? BuildEntries(doc)),
            ("createdUtc", doc.CreatedUtcRaw),
            ("updatedUtc", doc.UpdatedUtcRaw),
            ("lastOperationId", Q(doc.LastOperationId)),
        };

        var sb = new StringBuilder("{");
        bool first = true;
        foreach ((string name, string raw) in parts)
        {
            if (name == doc.Omit)
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
        sb.Append(doc.ExtraJson).Append('}');
        return sb.ToString();
    }

    private static string BuildEntries(DocModel doc)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < doc.Entries.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            sb.Append(BuildEntry(doc.Entries[i], doc.GenerationRaw));
        }
        return sb.Append(']').ToString();
    }

    private static string BuildEntry(EntryModel e, string generationRaw)
    {
        string binding = e.CapturedStateBindingOverride ?? ComputeBinding(e, generationRaw);

        var parts = new List<(string Name, string Raw)>
        {
            ("entryId", Q(e.EntryId)),
            ("owningUserSid", Q(e.OwningUserSid)),
            ("serviceSid", Q(e.ServiceSid)),
            ("credentialKind", Q(e.CredentialKind)),
            ("certificateThumbprintSha1", Q(e.CertificateThumbprintSha1)),
            ("provenance", Q(e.Provenance)),
            ("privateKeyProviderKind", Q(e.PrivateKeyProviderKind)),
            ("rightsProfileId", Q(e.RightsProfileId)),
            ("keyIdentity", Q(e.KeyIdentity)),
            ("grantMechanism", Q(e.GrantMechanism)),
            ("grantedRightsMask", Q(e.GrantedRightsMask)),
            ("rightsPolicyVersion", e.RightsPolicyVersionRaw),
            ("priorDaclState", Q(e.PriorDaclState)),
            ("priorDaclBytesBase64", Q(e.PriorDaclBytesBase64)),
            ("priorDaclSha256", Q(e.PriorDaclSha256)),
            ("capturedStateBindingSha256", Q(binding)),
            ("associatedPromotedJobIds", e.JobIdsRaw ?? "[" + string.Join(",", e.JobIds.Select(Q)) + "]"),
            ("lifecycleState", Q(e.LifecycleState)),
            ("createdUtc", Q(e.CreatedUtc)),
            ("updatedUtc", Q(e.UpdatedUtc)),
            ("providerUniqueName", Q(e.ProviderUniqueName)),
            ("keyStorageRoot", Q(e.KeyStorageRoot)),
            ("descriptorFormat", Q(e.DescriptorFormat)),
        };

        var sb = new StringBuilder("{");
        bool first = true;
        foreach ((string name, string raw) in parts)
        {
            if (name == e.Omit)
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
        return sb.Append(e.ExtraJson).Append('}').ToString();
    }

    private static string ComputeBinding(EntryModel e, string generationRaw)
    {
        int generation = int.TryParse(generationRaw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int g)
            ? g
            : 0;

        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.PrivateKeyProviderKind, out ServiceOwnershipPrivateKeyProviderKind provider);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.RightsProfileId, out ServiceOwnershipRightsProfileId profile);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.GrantMechanism, out ServiceOwnershipGrantMechanism mechanism);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.PriorDaclState, out ServiceOwnershipPriorDaclState priorState);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.KeyStorageRoot, out ServiceOwnershipKeyStorageRoot keyStorageRoot);
        ServiceOwnershipLedgerContract.TryParseWireToken(
            e.DescriptorFormat, out ServiceOwnershipDescriptorFormat descriptorFormat);

        return ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            generation,
            e.OwningUserSid,
            e.ServiceSid,
            e.CertificateThumbprintSha1,
            provider,
            profile,
            e.KeyIdentity,
            mechanism,
            e.GrantedRightsMask,
            priorState,
            e.PriorDaclSha256,
            e.ProviderUniqueName,
            keyStorageRoot,
            descriptorFormat);
    }

    // ===========================================================================
    // CYCLE 48w - GAP CLOSURE: DIRECT CONTRACT TESTS (G, H, I, J)
    // ===========================================================================
    //
    // Cycles 48t/48u/48v mapped four mutants the frozen suite could not kill.
    // Brian authorized DIRECT CONTRACT TESTS rather than deleting or wiring the two
    // uncalled functions (TryBuildPostGrantDescriptor, ClassifyObservedDescriptor):
    // they remain deliberate, unconsumed future observer/executor contracts. Every
    // bounded refusal below is proven against an ACCEPTED-NAME/SUPPORTED CONTROL so
    // the refusal is shown to be about the specific defect, not a broken fixture.

    // ---- GAP G: provider unique-name grammar ---------------------------------

    [Fact]
    public void Gap_G_control_the_accepted_provider_unique_name_passes_grammar_and_full_validation()
    {
        Assert.True(ServiceOwnershipLedgerContract.IsValidProviderUniqueName(ProviderUniqueNameValue));

        ServiceOwnershipLedgerValidationResult result =
            ServiceOwnershipLedgerValidator.Validate(Build(Doc()));

        Assert.True(result.IsAccepted);
        Assert.Equal(ProviderUniqueNameValue, result.Document!.Entries[0].ProviderUniqueName);
    }

    [Theory]
    [InlineData(".leading-dot-leaf")]
    [InlineData("trailing-dot-leaf.")]
    [InlineData("trailing-space-leaf ")]
    [InlineData("has..traversal.leaf")]
    [InlineData("CON")]
    [InlineData("CON.key")]
    public void Gap_G_provider_unique_name_grammar_rejects_each_bounded_defect(string value)
    {
        Assert.False(ServiceOwnershipLedgerContract.IsValidProviderUniqueName(value));

        DocModel doc = Doc();
        doc.Entries[0].ProviderUniqueName = value;
        Refused(Build(doc), ServiceOwnershipLedgerInvalidReason.InvalidProviderUniqueName);
    }

    // ---- GAP H: wrong-owner prior descriptor shape ---------------------------
    //
    // A test-local descriptor builder, independent of the contract's own encoder,
    // so it serves as an oracle rather than a mirror of the code under test.

    private const string PriorRequiredOwnerSid = "S-1-5-32-544";
    private const string PriorSystemSid = "S-1-5-18";
    private const string PriorCanonicalGroupSid = "S-1-5-21-1111111111-2222222222-3333333333-513";
    private const uint PriorFileFullControlRights = 0x001F01FFu;
    private const byte PriorRequiredAceFlags = 0x03; // ObjectInherit | ContainerInherit
    private const byte PriorAceTypeAccessAllowed = 0x00;
    private const ushort PriorControlFlags = 0x8000 | 0x0004 | 0x1000; // SelfRelative | DaclPresent | DaclProtected
    private const uint ExpectedPostGrantMask = 0x00120009u; // ReadData|ReadExtendedAttributes|ReadPermissions|Synchronize - derived here, never read from the contract

    private sealed class TestAceSpec
    {
        public byte Type;
        public byte Flags;
        public uint Mask;
        public string Sid = string.Empty;
    }

    private static byte[] EncodeTestSid(string sid)
    {
        string[] parts = sid.Split('-');
        ulong authority = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
        uint[] subs = parts.Skip(3).Select(p => uint.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        var bytes = new byte[8 + (4 * subs.Length)];
        bytes[0] = 1;
        bytes[1] = (byte)subs.Length;
        for (int i = 0; i < 6; i++)
        {
            bytes[2 + i] = (byte)((authority >> (8 * (5 - i))) & 0xFF);
        }
        for (int i = 0; i < subs.Length; i++)
        {
            WriteTestU32(bytes, 8 + (4 * i), subs[i]);
        }
        return bytes;
    }

    private static void WriteTestU32(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
        bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
        bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteTestU16(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    /// <summary>
    /// TEST-LOCAL descriptor builder. Encodes a self-relative file security
    /// descriptor byte-for-byte per the documented layout (20-byte header, 8-byte
    /// ACL header, per-ACE header/mask/SID), independent of the contract's own
    /// encoder, so it can serve as an INDEPENDENT oracle for expected bytes.
    /// </summary>
    private static byte[] BuildSelfRelativeDescriptor(
        string ownerSid, string groupSid, IReadOnlyList<TestAceSpec> aces, ushort control)
    {
        byte[] ownerBytes = EncodeTestSid(ownerSid);
        byte[] groupBytes = EncodeTestSid(groupSid);

        var aceBytesList = new List<byte[]>();
        foreach (TestAceSpec ace in aces)
        {
            byte[] sidBytes = EncodeTestSid(ace.Sid);
            ushort aceSize = (ushort)(8 + sidBytes.Length);
            var aceBytes = new byte[aceSize];
            aceBytes[0] = ace.Type;
            aceBytes[1] = ace.Flags;
            WriteTestU16(aceBytes, 2, aceSize);
            WriteTestU32(aceBytes, 4, ace.Mask);
            Array.Copy(sidBytes, 0, aceBytes, 8, sidBytes.Length);
            aceBytesList.Add(aceBytes);
        }

        ushort aclSize = (ushort)(8 + aceBytesList.Sum(a => a.Length));
        var dacl = new byte[aclSize];
        dacl[0] = 2; // ACL_REVISION
        dacl[1] = 0;
        WriteTestU16(dacl, 2, aclSize);
        WriteTestU16(dacl, 4, (ushort)aceBytesList.Count);
        int cursor = 8;
        foreach (byte[] aceBytes in aceBytesList)
        {
            Array.Copy(aceBytes, 0, dacl, cursor, aceBytes.Length);
            cursor += aceBytes.Length;
        }

        uint offsetOwner = 20;
        uint offsetGroup = offsetOwner + (uint)ownerBytes.Length;
        uint offsetDacl = offsetGroup + (uint)groupBytes.Length;

        var output = new byte[offsetDacl + aclSize];
        output[0] = 1; // revision
        output[1] = 0;
        WriteTestU16(output, 2, control);
        WriteTestU32(output, 4, offsetOwner);
        WriteTestU32(output, 8, offsetGroup);
        WriteTestU32(output, 12, 0); // offsetSacl - none
        WriteTestU32(output, 16, offsetDacl);
        Array.Copy(ownerBytes, 0, output, offsetOwner, ownerBytes.Length);
        Array.Copy(groupBytes, 0, output, offsetGroup, groupBytes.Length);
        Array.Copy(dacl, 0, output, offsetDacl, dacl.Length);
        return output;
    }

    private static byte[] BuildSupportedPriorDescriptorBytes(string headerOwnerSid = PriorRequiredOwnerSid) =>
        BuildSelfRelativeDescriptor(
            headerOwnerSid,
            PriorCanonicalGroupSid,
            new[]
            {
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorSystemSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorRequiredOwnerSid },
            },
            PriorControlFlags);

    [Fact]
    public void Gap_H_control_the_supported_owner_prior_descriptor_passes_shape_validation()
    {
        byte[] bytes = BuildSupportedPriorDescriptorBytes();

        bool parsed = ServiceOwnershipLedgerContract.TryParseFileSecurityDescriptor(
            bytes, out ServiceOwnershipParsedFileSecurityDescriptor? descriptor);
        Assert.True(parsed);
        Assert.NotNull(descriptor);
        Assert.Equal(PriorRequiredOwnerSid, descriptor!.OwnerSid);

        bool shapeOk = ServiceOwnershipLedgerContract.IsSupportedPriorDescriptorShape(
            descriptor, out ServiceOwnershipLedgerInvalidReason reason);
        Assert.True(shapeOk);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, reason);
    }

    [Fact]
    public void Gap_H_a_prior_descriptor_differing_only_in_owner_sid_parses_but_fails_shape_validation()
    {
        const string wrongOwnerSid = "S-1-5-32-545"; // BUILTIN\Users - differs only from the required owner
        byte[] bytes = BuildSupportedPriorDescriptorBytes(headerOwnerSid: wrongOwnerSid);

        bool parsed = ServiceOwnershipLedgerContract.TryParseFileSecurityDescriptor(
            bytes, out ServiceOwnershipParsedFileSecurityDescriptor? descriptor);
        Assert.True(parsed); // parsing is fine - the bytes are structurally valid
        Assert.NotNull(descriptor);
        Assert.Equal(wrongOwnerSid, descriptor!.OwnerSid);
        Assert.NotEqual(PriorRequiredOwnerSid, descriptor.OwnerSid);

        bool shapeOk = ServiceOwnershipLedgerContract.IsSupportedPriorDescriptorShape(
            descriptor, out ServiceOwnershipLedgerInvalidReason reason);
        Assert.False(shapeOk);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.PriorDescriptorUnsupportedShape, reason);
    }

    // ---- GAP I: TryBuildPostGrantDescriptor -----------------------------------

    [Fact]
    public void Gap_I_a_supported_prior_descriptor_yields_the_exact_independently_derived_output()
    {
        byte[] prior = BuildSupportedPriorDescriptorBytes();
        byte[] priorSnapshot = (byte[])prior.Clone();

        // Independently derived expected output: the SAME two prior ACEs, plus
        // EXACTLY one appended service ACE with flags 0x00 (no inherit-propagation
        // - a direct leaf grant) and mask 0x00120009. Built with the test's OWN
        // encoder; TryBuildPostGrantDescriptor is never called to produce this.
        byte[] expected = BuildSelfRelativeDescriptor(
            PriorRequiredOwnerSid,
            PriorCanonicalGroupSid,
            new[]
            {
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorSystemSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorRequiredOwnerSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = 0x00, Mask = ExpectedPostGrantMask, Sid = SvcSid },
            },
            PriorControlFlags);
        string expectedSha256 = Hex(SHA256.HashData(expected));

        bool ok = ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            prior, SvcSid, out byte[] postGrantBytes, out string postGrantSha256,
            out ServiceOwnershipLedgerInvalidReason reason);

        Assert.True(ok);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, reason);
        Assert.Equal(expected, postGrantBytes);
        Assert.Equal(expectedSha256, postGrantSha256);

        // The input array is unchanged (defensive copy).
        Assert.Equal(priorSnapshot, prior);

        // Determinism: a second call yields identical bytes and hash.
        bool ok2 = ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            prior, SvcSid, out byte[] postGrantBytes2, out string postGrantSha256b,
            out ServiceOwnershipLedgerInvalidReason reason2);
        Assert.True(ok2);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, reason2);
        Assert.Equal(postGrantBytes, postGrantBytes2);
        Assert.Equal(postGrantSha256, postGrantSha256b);
    }

    [Fact]
    public void Gap_I_the_appended_service_ace_carries_exactly_the_approved_mask_and_no_inheritance()
    {
        byte[] prior = BuildSupportedPriorDescriptorBytes();

        bool ok = ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            prior, SvcSid, out byte[] postGrantBytes, out _, out ServiceOwnershipLedgerInvalidReason reason);
        Assert.True(ok);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, reason);

        bool parsed = ServiceOwnershipLedgerContract.TryParseFileSecurityDescriptor(
            postGrantBytes, out ServiceOwnershipParsedFileSecurityDescriptor? descriptor);
        Assert.True(parsed);
        Assert.NotNull(descriptor);
        Assert.Equal(3, descriptor!.Aces.Count);

        ServiceOwnershipParsedAce appended = descriptor.Aces[2];
        Assert.Equal((byte)0x00, appended.AceType); // AccessAllowed
        Assert.Equal((byte)0x00, appended.AceFlags); // no inheritance - a direct leaf grant
        Assert.Equal(SvcSid, appended.Sid);

        // DRIFT DETECTION: fails if a required bit went missing, and fails if
        // ReadAttributes (0x80) were ever added to the approved mask.
        Assert.Equal(0x00120009u, appended.Mask);
        Assert.Equal(0u, appended.Mask & 0x00000080u); // FileReadAttributesRight must be absent
    }

    [Fact]
    public void Gap_I_a_prior_descriptor_carrying_an_unsupported_shape_is_refused()
    {
        byte[] wrongOwner = BuildSupportedPriorDescriptorBytes(headerOwnerSid: "S-1-5-32-545");

        bool ok = ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            wrongOwner, SvcSid, out byte[] postGrantBytes, out string postGrantSha256,
            out ServiceOwnershipLedgerInvalidReason reason);

        Assert.False(ok);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.PriorDescriptorUnsupportedShape, reason);
        Assert.Empty(postGrantBytes);
        Assert.Equal(string.Empty, postGrantSha256);
    }

    /// <summary>
    /// The ONE supported prior shape is locked to EXACTLY two ACEs (SYSTEM,
    /// BUILTIN\Administrators), and neither of those two fixed SIDs can ever
    /// satisfy <c>IsServiceVirtualAccountSid</c>. A THIRD ACE for a service SID
    /// therefore always trips the shape gate (<c>Aces.Count != 2</c>) before the
    /// already-granted check is ever reached under the current schema. This test
    /// proves that ordering directly, rather than asserting an outcome that is not
    /// reachable through the public surface given the one supported shape.
    /// </summary>
    [Fact]
    public void Gap_I_a_prior_descriptor_shape_gate_precedes_the_already_granted_check()
    {
        byte[] alreadyGranted = BuildSelfRelativeDescriptor(
            PriorRequiredOwnerSid, PriorCanonicalGroupSid,
            new[]
            {
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorSystemSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorRequiredOwnerSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = 0x00, Mask = ExpectedPostGrantMask, Sid = SvcSid },
            },
            PriorControlFlags);

        bool ok = ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            alreadyGranted, SvcSid, out byte[] postGrantBytes, out string postGrantSha256,
            out ServiceOwnershipLedgerInvalidReason reason);

        Assert.False(ok);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.PriorDescriptorUnsupportedShape, reason);
        Assert.Empty(postGrantBytes);
        Assert.Equal(string.Empty, postGrantSha256);
    }

    // ---- GAP J: ClassifyObservedDescriptor -------------------------------------

    [Fact]
    public void Gap_J_an_observed_descriptor_equal_to_the_captured_prior_matches_captured_prior_state()
    {
        byte[] prior = BuildSupportedPriorDescriptorBytes();
        byte[] observed = (byte[])prior.Clone();

        bool grantOk = ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            prior, SvcSid, out byte[] grant, out _, out _);
        Assert.True(grantOk);

        ServiceOwnershipDescriptorClassification classification =
            ServiceOwnershipLedgerContract.ClassifyObservedDescriptor(observed, prior, grant);

        Assert.Equal(ServiceOwnershipDescriptorClassification.MatchesCapturedPriorState, classification);
    }

    [Fact]
    public void Gap_J_an_observed_descriptor_equal_to_the_generated_grant_matches_recorded_grant()
    {
        byte[] prior = BuildSupportedPriorDescriptorBytes();
        bool grantOk = ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            prior, SvcSid, out byte[] grant, out _, out _);
        Assert.True(grantOk);
        byte[] observed = (byte[])grant.Clone();

        ServiceOwnershipDescriptorClassification classification =
            ServiceOwnershipLedgerContract.ClassifyObservedDescriptor(observed, prior, grant);

        Assert.Equal(ServiceOwnershipDescriptorClassification.MatchesRecordedGrant, classification);
    }

    [Fact]
    public void Gap_J_a_generic_read_bearing_variant_diverges()
    {
        byte[] prior = BuildSupportedPriorDescriptorBytes();
        ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(prior, SvcSid, out byte[] grant, out _, out _);

        byte[] observed = BuildSelfRelativeDescriptor(
            PriorRequiredOwnerSid, PriorCanonicalGroupSid,
            new[]
            {
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorSystemSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorRequiredOwnerSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = 0x00, Mask = ExpectedPostGrantMask | 0x80000000u, Sid = SvcSid }, // GenericRead added
            },
            PriorControlFlags);

        ServiceOwnershipDescriptorClassification classification =
            ServiceOwnershipLedgerContract.ClassifyObservedDescriptor(observed, prior, grant);

        Assert.Equal(ServiceOwnershipDescriptorClassification.Diverged, classification);
    }

    [Fact]
    public void Gap_J_a_superset_mask_variant_diverges()
    {
        byte[] prior = BuildSupportedPriorDescriptorBytes();
        ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(prior, SvcSid, out byte[] grant, out _, out _);

        byte[] observed = BuildSelfRelativeDescriptor(
            PriorRequiredOwnerSid, PriorCanonicalGroupSid,
            new[]
            {
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorSystemSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorRequiredOwnerSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = 0x00, Mask = ExpectedPostGrantMask | 0x00000080u, Sid = SvcSid }, // approved mask PLUS ReadAttributes
            },
            PriorControlFlags);

        ServiceOwnershipDescriptorClassification classification =
            ServiceOwnershipLedgerContract.ClassifyObservedDescriptor(observed, prior, grant);

        Assert.Equal(ServiceOwnershipDescriptorClassification.Diverged, classification);
    }

    [Fact]
    public void Gap_J_a_duplicate_service_ace_diverges()
    {
        byte[] prior = BuildSupportedPriorDescriptorBytes();
        ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(prior, SvcSid, out byte[] grant, out _, out _);

        byte[] observed = BuildSelfRelativeDescriptor(
            PriorRequiredOwnerSid, PriorCanonicalGroupSid,
            new[]
            {
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorSystemSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorRequiredOwnerSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = 0x00, Mask = ExpectedPostGrantMask, Sid = SvcSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = 0x00, Mask = ExpectedPostGrantMask, Sid = SvcSid }, // duplicate
            },
            PriorControlFlags);

        ServiceOwnershipDescriptorClassification classification =
            ServiceOwnershipLedgerContract.ClassifyObservedDescriptor(observed, prior, grant);

        Assert.Equal(ServiceOwnershipDescriptorClassification.Diverged, classification);
    }

    [Fact]
    public void Gap_J_a_foreign_service_ace_diverges()
    {
        byte[] prior = BuildSupportedPriorDescriptorBytes();
        ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(prior, SvcSid, out byte[] grant, out _, out _);

        const string foreignServiceSid = "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567891"; // differs from SvcSid only in the last digit
        byte[] observed = BuildSelfRelativeDescriptor(
            PriorRequiredOwnerSid, PriorCanonicalGroupSid,
            new[]
            {
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorSystemSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorRequiredOwnerSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = 0x00, Mask = ExpectedPostGrantMask, Sid = foreignServiceSid },
            },
            PriorControlFlags);

        ServiceOwnershipDescriptorClassification classification =
            ServiceOwnershipLedgerContract.ClassifyObservedDescriptor(observed, prior, grant);

        Assert.Equal(ServiceOwnershipDescriptorClassification.Diverged, classification);
    }

    [Fact]
    public void Gap_J_a_reordered_descriptor_diverges()
    {
        byte[] prior = BuildSupportedPriorDescriptorBytes();
        ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(prior, SvcSid, out byte[] grant, out _, out _);

        // The two prior ACEs swapped: same content, different order - structural
        // equality is index-wise, so this is a foreign descriptor, not the prior.
        byte[] observed = BuildSelfRelativeDescriptor(
            PriorRequiredOwnerSid, PriorCanonicalGroupSid,
            new[]
            {
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorRequiredOwnerSid },
                new TestAceSpec { Type = PriorAceTypeAccessAllowed, Flags = PriorRequiredAceFlags, Mask = PriorFileFullControlRights, Sid = PriorSystemSid },
            },
            PriorControlFlags);

        ServiceOwnershipDescriptorClassification classification =
            ServiceOwnershipLedgerContract.ClassifyObservedDescriptor(observed, prior, grant);

        Assert.Equal(ServiceOwnershipDescriptorClassification.Diverged, classification);
    }

    [Fact]
    public void Gap_J_malformed_observed_bytes_diverge_regardless_of_prior_or_grant()
    {
        byte[] prior = BuildSupportedPriorDescriptorBytes();
        ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(prior, SvcSid, out byte[] grant, out _, out _);

        byte[] malformed = { 0x02, 0x00, 0x00, 0x00 }; // too short to even carry a header

        ServiceOwnershipDescriptorClassification classification =
            ServiceOwnershipLedgerContract.ClassifyObservedDescriptor(malformed, prior, grant);

        Assert.Equal(ServiceOwnershipDescriptorClassification.Diverged, classification);
    }
}
