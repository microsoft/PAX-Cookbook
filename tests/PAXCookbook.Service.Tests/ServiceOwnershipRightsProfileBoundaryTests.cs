using System;
using System.IO;
using System.Linq;
using Xunit;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.Service.Tests;

// Cycle 42 - SERVICE-SIDE boundary coverage for the provider-specific rights
// profile added to the compile-linked ownership ledger contract (schema v2).
//
// WHAT AN APPROVED PROFILE DOES NOT GIVE THE SERVICE. Compile-linking a schema that
// now CONTAINS an approved rights profile grants the service host no certificate
// store, no private key handle, no ACL API, no registry handle, no credential
// vault, no service-control channel, no elevation path, no network stack, and still
// no ledger reader, writer, observer or executor. This file exists to keep that
// honest now that HasApprovedRightsProfile is true.
//
// Nothing here installs, registers, starts or contacts a service, touches machine
// state, spawns a process, runs PAX or starts a Bake.
public class ServiceOwnershipRightsProfileBoundaryTests
{
    [Fact]
    public void The_new_profile_vocabulary_travels_with_the_linked_contract()
    {
        Assert.Equal(
            typeof(ServiceContract).Assembly,
            typeof(ServiceOwnershipRightsProfileId).Assembly);

        Assert.Equal(
            "PAXCookbook.Service",
            typeof(ServiceOwnershipRightsProfileId).Assembly.GetName().Name);

        // The exact provider classification travels too, and it is DISTINCT from the
        // broad classifications it must never be confused with.
        string[] providers = Enum.GetNames(typeof(ServiceOwnershipPrivateKeyProviderKind));
        Assert.Contains("MicrosoftSoftwareKeyStorageProvider", providers);
        Assert.Contains("Cng", providers);
        Assert.Contains("LegacyCsp", providers);
        Assert.Equal(4, providers.Length);
    }

    [Fact]
    public void The_linked_schema_is_version_three_inside_the_service_host()
    {
        Assert.Equal(3, ServiceOwnershipLedgerContract.LedgerSchemaVersion);
        Assert.Equal(3, ServiceOwnershipLedgerContract.RightsPolicyVersion);

        // An approved profile now exists - and that is the ONLY thing it means.
        Assert.True(ServiceOwnershipLedgerContract.HasApprovedRightsProfile);
        Assert.Equal(0x00120009u, ServiceOwnershipLedgerContract.ApprovedRightsProfileMask);
        Assert.Equal(
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileProviderKind);
        Assert.Equal(
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileGrantMechanism);
    }

    [Fact]
    public void The_linked_authorizer_accepts_only_the_exact_profile_inside_the_service_host()
    {
        Assert.True(ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
            new ServiceOwnershipRightsMask(0x00120009u),
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            3,
            ServiceOwnershipLedgerContract.ApprovedDescriptorFormat,
            out ServiceOwnershipLedgerInvalidReason ok));
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.None, ok);

        // The retired CNG mechanism is refused by its own reason, not a mismatch.
        Assert.False(ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
            new ServiceOwnershipRightsMask(0x00120009u),
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.CngSecurityDescriptor,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            3,
            ServiceOwnershipLedgerContract.ApprovedDescriptorFormat,
            out ServiceOwnershipLedgerInvalidReason retired));
        Assert.Equal(
            ServiceOwnershipLedgerInvalidReason.RetiredCngSecurityDescriptorMechanismUnsupported,
            retired);

        // Broad CNG cannot inherit it here either.
        Assert.False(ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
            new ServiceOwnershipRightsMask(0x00120009u),
            ServiceOwnershipPrivateKeyProviderKind.Cng,
            ServiceOwnershipGrantMechanism.CngSecurityDescriptor,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            3,
            ServiceOwnershipLedgerContract.ApprovedDescriptorFormat,
            out ServiceOwnershipLedgerInvalidReason broad));
        Assert.Equal(
            ServiceOwnershipLedgerInvalidReason.RetiredCngSecurityDescriptorMechanismUnsupported,
            broad);

        // GenericRead is refused by its own reason rather than normalised.
        Assert.False(ServiceOwnershipLedgerContract.TryAuthorizeActiveRights(
            new ServiceOwnershipRightsMask(0x80000000u),
            ServiceOwnershipPrivateKeyProviderKind.MicrosoftSoftwareKeyStorageProvider,
            ServiceOwnershipGrantMechanism.MicrosoftSoftwareKspBackingFileDacl,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            3,
            ServiceOwnershipLedgerContract.ApprovedDescriptorFormat,
            out ServiceOwnershipLedgerInvalidReason generic));
        Assert.Equal(
            ServiceOwnershipLedgerInvalidReason.GenericReadRightNotAuthorized,
            generic);
    }

    [Fact]
    public void The_linked_validator_reports_the_new_active_outcome_inside_the_service_host()
    {
        // Cycle 82: active acceptance exists, at the NEW value 9. The RETIRED slot-2
        // name must still be gone and slot 2 itself must still be empty, so the
        // linked copy cannot have resurrected the old success state by the back door.
        Assert.DoesNotContain("ValidActive", Enum.GetNames(typeof(ServiceOwnershipLedgerOutcome)));
        Assert.False(Enum.IsDefined(typeof(ServiceOwnershipLedgerOutcome), 2));
        Assert.Equal(9, (int)ServiceOwnershipLedgerOutcome.Active);

        // The terminal active-lifecycle refusal is KEPT in the bounded vocabulary and
        // still maps to a refusal, even though the parser no longer produces it.
        Assert.Contains(
            "ActiveLifecycleAcceptanceNotAuthorized",
            Enum.GetNames(typeof(ServiceOwnershipLedgerInvalidReason)));
        Assert.Equal(
            ServiceOwnershipLedgerOutcome.Inconsistent,
            ServiceOwnershipLedgerContract.MapRefusalOutcome(
                ServiceOwnershipLedgerInvalidReason.ActiveLifecycleAcceptanceNotAuthorized));

        foreach (string? hostile in new string?[] { null, "", "   ", "{}", "[]", "not json" })
        {
            ServiceOwnershipLedgerValidationResult result =
                ServiceOwnershipLedgerValidator.Validate(hostile);
            Assert.True(result.IsRefused);
            Assert.Null(result.Document);
        }
    }

    [Fact]
    public void The_linked_planner_still_refuses_promotion_with_zero_actions()
    {
        ServiceOwnershipLedgerValidationResult absent = ServiceOwnershipLedgerValidator.ForAbsentLedger();

        ServiceOwnershipLifecyclePlanRequest? assess =
            ServiceOwnershipLifecyclePlanRequest.ForAssessPromotion(
                absent, "install-0001",
                "S-1-5-21-1111111111-2222222222-3333333333-1001",
                "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890");
        Assert.NotNull(assess);

        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(assess);
        Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
        Assert.Equal(ServiceOwnershipPlanRefusalReason.PromotionNotAuthorized, plan.RefusalReason);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void No_service_source_file_names_the_new_profile_or_provider_vocabulary()
    {
        string[] tokens =
        {
            "ServiceOwnershipRightsProfileId",
            "MicrosoftSoftwareKeyStorageProvider",
            "ApprovedRightsProfileMask",
            "TryAuthorizeActiveRights",
        };

        string[] offenders = ServiceCookPreparationBoundaryTests.ServiceSourceFiles()
            .Where(f => tokens.Any(t => ServiceCookPreparationBoundaryTests
                .StripComments(File.ReadAllText(f))
                .Contains(t, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToArray();

        Assert.Empty(offenders);

        // POSITIVE CONTROL. Without this, "0 offenders" is unfalsifiable: a broken
        // scanner and a clean service look identical.
        foreach (string token in tokens)
        {
            Assert.Contains(
                token,
                ServiceCookPreparationBoundaryTests.StripComments("var x = " + token + ";"),
                StringComparison.Ordinal);
        }

        // NEGATIVE CONTROL with teeth: a comment does NOT count as a reference.
        Assert.DoesNotContain(
            "ServiceOwnershipRightsProfileId",
            ServiceCookPreparationBoundaryTests.StripComments("// ServiceOwnershipRightsProfileId"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_profile_added_no_grant_apply_or_machine_mutation_verb_to_the_service_vocabulary()
    {
        var enumMembers = typeof(ServiceContract).Assembly
            .GetTypes()
            .Where(t => t.IsEnum)
            .SelectMany(t => Enum.GetNames(t))
            .ToArray();

        Assert.NotEmpty(enumMembers);

        foreach (string forbidden in new[]
                 {
                     "ApplyGrant", "GrantAccess", "GrantRights", "ApplyRightsProfile",
                     "ImportCertificate", "StartService", "RunCook",
                     "DeleteReferencedCertificate", "PersistRightsProfile",
                 })
        {
            Assert.DoesNotContain(forbidden, enumMembers);
        }

        // POSITIVE CONTROL: the enumeration really did see the new vocabulary.
        Assert.Contains(
            "MicrosoftSoftwareKspRsa2048Ps256AzureIdentity1_18_0Msal4_82_1GraphAuth2_39_0",
            enumMembers);
    }
}
