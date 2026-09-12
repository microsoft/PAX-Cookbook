using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 88 - SHARED SYNTHETIC FIXTURES FOR THE NON-LIVE PROMOTION TRANSACTION
// ===========================================================================
//
// Every value here is SYNTHETIC. Nothing in this file opens a certificate store,
// touches a private key, reads or writes an ACL, reads the registry, installs or
// starts a service, elevates, writes ProgramData, opens a socket or starts a
// process.
internal static class ServiceOwnershipPromotionFixtures
{
    internal const string OwnerSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    internal const string SvcSid = "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890";
    internal const string Thumb = "ABCDEF0123456789ABCDEF0123456789ABCDEF01";
    internal const string KeyId = "synthetic-key-identity_01.test";
    internal const string ProviderUniqueNameValue = "synthetic-unique-leaf_01.pvk";
    internal const string KeyStorageRootToken = "microsoft-software-key-storage-provider-machine-keys";
    internal const string CredentialKindToken = "personal-app-registration-certificate";
    internal const string ProvenanceToken = "referenced";
    internal const string Stamp = "2026-08-06T00:00:00Z";
    internal const string InstallId = "install-0001";
    internal const string EntryId = "entry-0001";
    internal const string JobId = "job-0001";
    internal const string OperationId = "op-0001";

    internal static readonly byte[] PriorBytes =
        { 0x01, 0x00, 0x04, 0x90, 0x14, 0x00, 0x00, 0x00, 0x24, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00, 0x20, 0x02, 0x00, 0x00, 0x01, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x15, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0xE9, 0x03, 0x00, 0x00, 0x02, 0x00, 0x34, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x03, 0x14, 0x00, 0xFF, 0x01, 0x1F, 0x00, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x12, 0x00, 0x00, 0x00, 0x00, 0x03, 0x18, 0x00, 0xFF, 0x01, 0x1F, 0x00, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x20, 0x00, 0x00, 0x00, 0x20, 0x02, 0x00, 0x00 };

    internal static string PriorB64 => Convert.ToBase64String(PriorBytes);

    internal static string PriorSha256 => ToUpperHex(SHA256.HashData(PriorBytes));

    internal static byte[] RecipeBytes =>
        new UTF8Encoding(false, true).GetBytes(RecipeJson);

    /// <summary>
    /// The SAME shape the accepted-request suite uses: a semantically valid Recipe
    /// whose auth mode is the one accepted spelling. Everything in it is synthetic.
    /// </summary>
    private const string RecipeJson = """
    {
      "recipeId": "01ARZ3NDEKTSV4RRFFQ69G5FAV",
      "recipeSchemaVersion": 1,
      "paxAdapterVersion": "1.11.11",
      "identity": { "name": "Cycle 88 synthetic fixture" },
      "ingredients": {
        "m365Usage": { "includeM365Usage": false },
        "entraUserData": { "includeUserInfo": false }
      },
      "query": { "mode": "audit", "dateMode": "previous-day" },
      "processing": {},
      "destinations": { "fact": { "mode": "outputPath", "path": "C:\\PAX\\audit.csv" } },
      "auth": { "mode": "AppRegistrationCertificate", "tenantId": "11111111-2222-3333-4444-555555555555" }
    }
    """;

    internal static string RecipeSha256 => ToUpperHex(SHA256.HashData(RecipeBytes));

    internal static string ToUpperHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // ---- the bounded key handle and captured descriptor ----------------------

    internal static ServiceOwnershipPromotionKeyHandle KeyHandle()
    {
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            KeyStorageRootToken, out ServiceOwnershipKeyStorageRoot keyStorageRoot));

        return ServiceOwnershipPromotionKeyHandle.Create(
            KeyId,
            ProviderUniqueNameValue,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileProviderKind,
            keyStorageRoot,
            ServiceOwnershipLedgerContract.ApprovedDescriptorFormat);
    }

    internal static ServiceOwnershipCertificateFacts CertificateFacts()
    {
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            CredentialKindToken, out ServiceOwnershipCredentialKind credentialKind));
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            ProvenanceToken, out ServiceOwnershipProvenance provenance));

        return ServiceOwnershipCertificateFacts.Observed(
            Thumb,
            credentialKind,
            provenance,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileGrantMechanism,
            KeyHandle());
    }

    internal static ServiceOwnershipCapturedPriorDescriptor Captured()
    {
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            "present", out ServiceOwnershipPriorDaclState present));

        return ServiceOwnershipCapturedPriorDescriptor.Captured(present, PriorB64, PriorSha256);
    }

    internal static ServiceOwnershipPromotionGrantPlan GrantPlan()
    {
        Assert.True(ServiceOwnershipPromotionGrantPlan.TryCreate(
            ServiceOwnershipServiceSidObservation.Resolved(SvcSid),
            KeyHandle(),
            out ServiceOwnershipPromotionGrantPlan plan));
        return plan;
    }

    // ---- the accepted promotion request --------------------------------------

    internal static ServicePromotionRequest PromotionRequest()
    {
        string json =
            "{\"schemaVersion\":1,"
            + "\"operationId\":\"" + OperationId + "\","
            + "\"promotedJobId\":\"" + JobId + "\","
            + "\"certificateThumbprintSha1\":\"" + Thumb + "\","
            + "\"recipeBase64\":\"" + Convert.ToBase64String(RecipeBytes) + "\","
            + "\"expectedRecipeSha256\":\"" + RecipeSha256 + "\","
            + "\"expectedInstallationOwnershipId\":\"" + InstallId + "\"}";

        ServicePromotionRequestParseResult parsed = ServicePromotionRequestParser.ParsePromotion(json);
        Assert.True(
            parsed.Promotion is not null,
            "the synthetic promotion request fixture must parse: " + parsed.Outcome);
        return parsed.Promotion!;
    }

    // ---- the pure transition chain -------------------------------------------

    internal static ServiceOwnershipTransitionFacts TransitionFacts()
    {
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            CredentialKindToken, out ServiceOwnershipCredentialKind credentialKind));
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            ProvenanceToken, out ServiceOwnershipProvenance provenance));
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            KeyStorageRootToken, out ServiceOwnershipKeyStorageRoot keyStorageRoot));
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            "present", out ServiceOwnershipPriorDaclState present));

        Assert.True(ServiceOwnershipTransitionFacts.TryCreate(
            EntryId, InstallId, OwnerSid, SvcSid, credentialKind, Thumb, provenance,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileProviderKind,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileId, KeyId,
            ServiceOwnershipLedgerContract.ApprovedRightsProfileGrantMechanism,
            new ServiceOwnershipRightsMask(ServiceOwnershipLedgerContract.ApprovedRightsProfileMask),
            ServiceOwnershipLedgerContract.RightsPolicyVersion,
            present, PriorB64, PriorSha256, JobId, ProviderUniqueNameValue, keyStorageRoot,
            ServiceOwnershipLedgerContract.ApprovedDescriptorFormat,
            out ServiceOwnershipTransitionFacts? facts));

        return facts!;
    }

    internal static ServiceOwnershipTransitionResult BeginIntent()
    {
        Assert.True(ServiceOwnershipTransitionRequest.TryCreate(
            ServiceOwnershipTransitionOperation.BeginPromotionIntent,
            ServiceOwnershipLedgerValidator.ForAbsentLedger(),
            TransitionFacts(), EntryId, string.Empty, OperationId, Stamp,
            out ServiceOwnershipTransitionRequest? request));

        ServiceOwnershipTransitionResult result = ServiceOwnershipTransitionAuthority.Apply(request);
        Assert.True(result.IsTransitioned, "BeginPromotionIntent: " + result.Outcome);
        return result;
    }

    internal static ServiceOwnershipTransitionResult RecordMutated()
    {
        Assert.True(ServiceOwnershipTransitionRequest.TryCreate(
            ServiceOwnershipTransitionOperation.RecordCredentialMutated,
            BeginIntent().Accepted!, null, EntryId, string.Empty, OperationId, Stamp,
            out ServiceOwnershipTransitionRequest? request));

        ServiceOwnershipTransitionResult result = ServiceOwnershipTransitionAuthority.Apply(request);
        Assert.True(result.IsTransitioned, "RecordCredentialMutated: " + result.Outcome);
        return result;
    }

    internal static ServiceOwnershipLedgerValidationResult ActiveLedger()
    {
        Assert.True(ServiceOwnershipTransitionRequest.TryCreate(
            ServiceOwnershipTransitionOperation.CompletePromotion,
            RecordMutated().Accepted!, null, EntryId, string.Empty, OperationId, Stamp,
            out ServiceOwnershipTransitionRequest? request));

        ServiceOwnershipTransitionResult result = ServiceOwnershipTransitionAuthority.Apply(request);
        Assert.True(result.IsTransitioned, "CompletePromotion: " + result.Outcome);
        return result.Accepted!;
    }
}

// ===========================================================================
// DETERMINISTIC PORT FAKES - every one is a pure, journalled stub
// ===========================================================================

internal sealed class FakeOwnerIdentityPort : IServiceOwnershipOwnerIdentityPort
{
    internal ServiceOwnershipOwnerIdentityObservation Next { get; set; } =
        ServiceOwnershipOwnerIdentityObservation.Observed(ServiceOwnershipPromotionFixtures.OwnerSid);

    internal int Calls { get; private set; }

    public ServiceOwnershipOwnerIdentityObservation ObserveFixedOwner()
    {
        Calls++;
        return Next;
    }
}

internal sealed class FakeServiceSidPort : IServiceOwnershipServiceSidPort
{
    internal ServiceOwnershipServiceSidObservation Next { get; set; } =
        ServiceOwnershipServiceSidObservation.Resolved(ServiceOwnershipPromotionFixtures.SvcSid);

    internal int Calls { get; private set; }

    public ServiceOwnershipServiceSidObservation ObserveFixedServiceSid()
    {
        Calls++;
        return Next;
    }
}

internal sealed class FakeCertificateFactsPort : IServiceOwnershipCertificateFactsPort
{
    internal ServiceOwnershipCertificateFacts Next { get; set; } =
        ServiceOwnershipPromotionFixtures.CertificateFacts();

    internal int Calls { get; private set; }

    internal string? LastThumbprint { get; private set; }

    public ServiceOwnershipCertificateFacts ObserveCertificateFacts(string normalizedThumbprintSha1)
    {
        Calls++;
        LastThumbprint = normalizedThumbprintSha1;
        return Next;
    }
}

internal sealed class FakePriorDescriptorPort : IServiceOwnershipPriorDescriptorPort
{
    internal ServiceOwnershipCapturedPriorDescriptor Next { get; set; } =
        ServiceOwnershipPromotionFixtures.Captured();

    internal int Calls { get; private set; }

    public ServiceOwnershipCapturedPriorDescriptor CapturePriorDescriptor(
        ServiceOwnershipPromotionKeyHandle key)
    {
        Calls++;
        return Next;
    }
}

internal sealed class FakeApprovedDescriptorPort : IServiceOwnershipApprovedDescriptorPort
{
    internal ServiceOwnershipDescriptorApplyState Next { get; set; } =
        ServiceOwnershipDescriptorApplyState.Applied;

    internal int Calls { get; private set; }

    /// <summary>Every captured snapshot this port was handed, in order.</summary>
    internal List<ServiceOwnershipCapturedPriorDescriptor> ReceivedCaptured { get; } = new();

    public ServiceOwnershipDescriptorApplyState ApplyApprovedDescriptor(
        ServiceOwnershipPromotionGrantPlan plan,
        ServiceOwnershipCapturedPriorDescriptor captured)
    {
        Calls++;
        ReceivedCaptured.Add(captured);
        return Next;
    }
}

internal sealed class FakeDescriptorRestorePort : IServiceOwnershipDescriptorRestorePort
{
    internal ServiceOwnershipDescriptorRestoreState Next { get; set; } =
        ServiceOwnershipDescriptorRestoreState.RestoredAndVerified;

    internal int Calls { get; private set; }

    public ServiceOwnershipDescriptorRestoreState RestoreCapturedDescriptor(
        ServiceOwnershipPromotionKeyHandle key,
        ServiceOwnershipCapturedPriorDescriptor captured)
    {
        Calls++;
        return Next;
    }
}

/// <summary>
/// A SCRIPTED credential observer: it returns the next scripted value on each
/// call, so a failure can be placed at an EXACT step of the sequence. It also
/// RECORDS the certified entry it was handed on every call, which is what makes
/// the cycle-90 compose-once reference-identity proof possible.
/// </summary>
internal sealed class FakeCredentialObservationPort : IServiceOwnershipCredentialObservationPort
{
    private readonly Queue<ServiceOwnershipCredentialObservation> script = new();

    internal int Calls { get; private set; }

    /// <summary>Every certified entry OBJECT this port was handed, in order.</summary>
    internal List<ServiceOwnershipLedgerEntry> ObservedEntries { get; } = new();

    internal ServiceOwnershipCredentialObservation Fallback { get; set; } =
        ServiceOwnershipCredentialObservation.Diverged;

    internal FakeCredentialObservationPort Script(
        params ServiceOwnershipCredentialObservation[] observations)
    {
        foreach (ServiceOwnershipCredentialObservation observation in observations)
        {
            script.Enqueue(observation);
        }
        return this;
    }

    public ServiceOwnershipCredentialObservation ObserveCredential(ServiceOwnershipLedgerEntry entry)
    {
        Calls++;
        ObservedEntries.Add(entry);
        return script.Count > 0 ? script.Dequeue() : Fallback;
    }
}

/// <summary>
/// A SCRIPTED ledger persistence port. It records every generation it was asked
/// to persist, so ORDER and MONOTONICITY are observable, and every serialization
/// OBJECT, so "the bytes persisted are the bytes that were composed" is testable
/// by reference rather than by value - two independent compositions of the same
/// pure transition produce IDENTICAL bytes, so only object identity can tell
/// them apart.
/// </summary>
internal sealed class FakeLedgerPersistencePort : IServiceOwnershipLedgerPersistencePort
{
    private readonly Queue<ServiceOwnershipLedgerPersistState> script = new();

    internal List<int> PersistedGenerations { get; } = new();

    internal List<ServiceOwnershipLedgerOutcome> PersistedOutcomes { get; } = new();

    internal List<ServiceOwnershipLedgerSerializationResult> PersistedSerializations { get; } = new();

    internal ServiceOwnershipLedgerPersistState Fallback { get; set; } =
        ServiceOwnershipLedgerPersistState.Persisted;

    internal FakeLedgerPersistencePort Script(params ServiceOwnershipLedgerPersistState[] states)
    {
        foreach (ServiceOwnershipLedgerPersistState state in states)
        {
            script.Enqueue(state);
        }
        return this;
    }

    public ServiceOwnershipLedgerPersistState Persist(
        ServiceOwnershipLedgerSerializationResult serialized,
        ServiceOwnershipLedgerOutcome expectedOutcome,
        int expectedGeneration)
    {
        PersistedGenerations.Add(expectedGeneration);
        PersistedOutcomes.Add(expectedOutcome);
        PersistedSerializations.Add(serialized);
        return script.Count > 0 ? script.Dequeue() : Fallback;
    }
}

internal sealed class FakePromotedRecipePort : IServiceOwnershipPromotedRecipePort
{
    internal ServiceOwnershipPromotedRecipePortOutcome NextPersist { get; set; } =
        ServiceOwnershipPromotedRecipePortOutcome.Completed;

    internal int PersistCalls { get; private set; }

    internal int CompensateCalls { get; private set; }

    public ServiceOwnershipPromotedRecipePortOutcome PersistPromotedRecipe(
        ServicePromotionRequest request)
    {
        PersistCalls++;
        return NextPersist;
    }

    public ServiceOwnershipPromotedRecipePortOutcome CompensatePromotedRecipe(string promotedJobId)
    {
        CompensateCalls++;
        return ServiceOwnershipPromotedRecipePortOutcome.Completed;
    }
}

/// <summary>
/// The REAL durable write surface behind the executor's ledger port, bound to an
/// OS-temp containment root. This is the only place the two product files this
/// cycle added are wired together, and it lives in the test project on purpose:
/// no production composition root exists.
/// </summary>
internal sealed class ContainedLedgerPersistencePort : IServiceOwnershipLedgerPersistencePort
{
    private readonly string containmentRoot;

    internal ContainedLedgerPersistencePort(string containmentRoot)
    {
        this.containmentRoot = containmentRoot;
    }

    internal int Calls { get; private set; }

    public ServiceOwnershipLedgerPersistState Persist(
        ServiceOwnershipLedgerSerializationResult serialized,
        ServiceOwnershipLedgerOutcome expectedOutcome,
        int expectedGeneration)
    {
        Calls++;

        ServiceOwnershipLedgerWriteResult result = ServiceOwnershipLedgerWriter.WriteWithinContainmentRoot(
            containmentRoot, serialized, expectedOutcome, expectedGeneration);

        if (result.IsWritten)
        {
            return ServiceOwnershipLedgerPersistState.Persisted;
        }

        if (result.Outcome == ServiceOwnershipLedgerWriteOutcome.RecoveryRequired)
        {
            return ServiceOwnershipLedgerPersistState.RecoveryRequired;
        }

        return result.PriorStateRestored
            ? ServiceOwnershipLedgerPersistState.RolledBack
            : ServiceOwnershipLedgerPersistState.Refused;
    }
}
