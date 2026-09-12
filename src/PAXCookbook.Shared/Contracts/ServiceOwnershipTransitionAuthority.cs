using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PAXCookbook.Shared.Contracts;

// ---------------------------------------------------------------------------
// PURE LEDGER TRANSITION AUTHORITY - cycle 88.
//
// WHAT THIS IS. The ONE place in the product that may compose canonical
// schema-v3 ownership-ledger JSON. It turns ALREADY-ACCEPTED typed ledger state
// plus a CLOSED operation plus BOUNDED facts into the NEXT accepted state, and
// returns it both as an accepted validation result and as serialized bytes.
//
// WHY IT COMPOSES JSON AT ALL. ServiceOwnershipLedgerEntry and
// ServiceOwnershipLedgerDocument have INTERNAL constructors and
// ServiceOwnershipLedgerValidationResult has a PRIVATE one, so no caller can
// assemble a document directly. The ONLY route to an accepted result is the
// public validator. This type therefore builds the canonical document text
// INTERNALLY and passes it through ServiceOwnershipLedgerValidator. That is the
// intended shape of the constraint - no loose ledger JSON may exist ELSEWHERE -
// not a way around it.
//
// WHAT IT IS NOT. It is PURE. It opens no file, composes no path, reads no
// environment variable or clock, holds no delegate, callback, strategy or
// mutable state, touches no certificate store, private key, ACL, registry,
// process or socket, and references no Windows-only or native type. Every
// timestamp and every identifier is supplied by the caller. Nothing in the
// product calls it.
//
// THE PROPERTY ORDER IS MIRRORED, NOT RETYPED. Both emitters below iterate the
// parser's OWN closed property lists - ServiceOwnershipLedgerContract
// .DocumentPropertyNames and .EntryPropertyNames - and switch on each name. A
// schema field added to the parser without being added here lands on the default
// arm and the WHOLE transition is REFUSED rather than silently emitted without
// it. A silently dropped priorDaclBytesBase64 would be an unrecoverable
// private-key ACL, so refusing is the only safe direction.
//
// NO UNBOUNDED VALUE CAN ESCAPE. Every emitted string is checked against the
// bounded ASCII charset the parser accepts. A value that would require JSON
// escaping - a quote, a backslash, a control character, anything non-ASCII - is
// REFUSED rather than escaped, so no exception text, path or hostile payload can
// ride out through a value the parser would have refused on the way in.
//
// EVERY SUCCESSFUL TRANSITION IS PROVEN FOUR TIMES.
//   1. The composed text is validated through ServiceOwnershipLedgerValidator.
//   2. Its outcome must equal the outcome this authority INDEPENDENTLY predicted
//      from the target state. A disagreement refuses.
//   3. It is serialized through ServiceOwnershipLedgerSerializer.
//   4. The serialized bytes are re-validated and must report the SAME outcome
//      and the SAME generation.
// Nothing is returned that has not survived all four.
//
// THE GENERATION ALWAYS ADVANCES, AND THAT IS WHY BINDINGS ARE RECOMPUTED.
// capturedStateBindingSha256 is a function OF the generation, so a generation
// bump necessarily changes every entry's binding. The binding is DERIVED state,
// not a field to carry forward; every other captured field is copied verbatim.
// ---------------------------------------------------------------------------

/// <summary>
/// The CLOSED set of ledger transitions. Zero is never a transition, so an
/// uninitialised value can never be mistaken for a legal operation.
/// </summary>
public enum ServiceOwnershipTransitionOperation
{
    Unspecified = 0,

    /// <summary>Absent or ValidEmpty -> Preparing, with one new Intended entry.</summary>
    BeginPromotionIntent = 1,

    /// <summary>Preparing + Intended -> CredentialMutated + Intended.</summary>
    RecordCredentialMutated = 2,

    /// <summary>CredentialMutated + Intended -> Done + Active.</summary>
    CompletePromotion = 3,

    /// <summary>Active -> Restoring, and the document enters the Restoring transaction.</summary>
    BeginRestore = 4,

    /// <summary>Restoring -> Restored, and the document returns to Done.</summary>
    MarkRestored = 5,

    /// <summary>Drops a Restored entry. An empty ledger returns to Idle.</summary>
    RemoveRestoredEntry = 6,

    /// <summary>Drops an abandoned Intended entry. An empty ledger returns to Idle.</summary>
    RemoveAbandonedIntentEntry = 7,

    /// <summary>Drops ONE promoted-job association from one entry. Nothing else changes.</summary>
    RemoveJobAssociation = 8,

    /// <summary>
    /// Marks one entry Stale, preserving EVERY captured fact. It removes nothing,
    /// restores nothing, and never reports completion.
    /// </summary>
    MarkStale = 9,
}

/// <summary>Bounded transition outcome. Zero always refuses.</summary>
public enum ServiceOwnershipTransitionOutcome
{
    Unspecified = 0,

    /// <summary>The next state was composed, validated, serialized and re-validated.</summary>
    Transitioned = 1,

    /// <summary>The request was null or structurally unusable.</summary>
    InvalidRequest = 2,

    /// <summary>The source ledger was null, refused, or not the shape this operation starts from.</summary>
    SourceNotAccepted = 3,

    /// <summary>The source ledger outcome is not a legal starting point for this operation.</summary>
    SourceStateMismatch = 4,

    /// <summary>The named entry does not exist, or is not in the required lifecycle state.</summary>
    TargetEntryUnusable = 5,

    /// <summary>The named promoted job is not associated with the named entry.</summary>
    TargetJobUnusable = 6,

    /// <summary>Bounded facts are required for this operation and were absent or unusable.</summary>
    FactsUnusable = 7,

    /// <summary>A schema property has no emitter here, so the output would be incomplete.</summary>
    UnmappedSchemaProperty = 8,

    /// <summary>A value is outside the bounded vocabulary the parser accepts.</summary>
    UnserializableValue = 9,

    /// <summary>The composed document was refused by the ledger validator.</summary>
    ComposedDocumentRefused = 10,

    /// <summary>The validator accepted a DIFFERENT outcome than this authority predicted.</summary>
    ComposedOutcomeMismatch = 11,

    /// <summary>The accepted document could not be serialized.</summary>
    SerializationRefused = 12,

    /// <summary>The serialized bytes did not re-validate to the same accepted state.</summary>
    RoundTripRefused = 13,
}

/// <summary>
/// The bounded, closed facts a NEW ownership entry is built from. The only
/// constructor is PRIVATE, so an instance exists only if <see cref="TryCreate"/>
/// accepted every field. No path, delegate, callback, stream, native handle or
/// environment value is representable.
/// </summary>
public sealed class ServiceOwnershipTransitionFacts
{
    private ServiceOwnershipTransitionFacts(
        string entryId,
        string installationOwnershipId,
        string owningUserSid,
        string serviceSid,
        ServiceOwnershipCredentialKind credentialKind,
        string certificateThumbprintSha1,
        ServiceOwnershipProvenance provenance,
        ServiceOwnershipPrivateKeyProviderKind privateKeyProviderKind,
        ServiceOwnershipRightsProfileId rightsProfileId,
        string keyIdentity,
        ServiceOwnershipGrantMechanism grantMechanism,
        ServiceOwnershipRightsMask grantedRightsMask,
        int rightsPolicyVersion,
        ServiceOwnershipPriorDaclState priorDaclState,
        string priorDaclBytesBase64,
        string priorDaclSha256,
        string promotedJobId,
        string providerUniqueName,
        ServiceOwnershipKeyStorageRoot keyStorageRoot,
        ServiceOwnershipDescriptorFormat descriptorFormat)
    {
        EntryId = entryId;
        InstallationOwnershipId = installationOwnershipId;
        OwningUserSid = owningUserSid;
        ServiceSid = serviceSid;
        CredentialKind = credentialKind;
        CertificateThumbprintSha1 = certificateThumbprintSha1;
        Provenance = provenance;
        PrivateKeyProviderKind = privateKeyProviderKind;
        RightsProfileId = rightsProfileId;
        KeyIdentity = keyIdentity;
        GrantMechanism = grantMechanism;
        GrantedRightsMask = grantedRightsMask;
        RightsPolicyVersion = rightsPolicyVersion;
        PriorDaclState = priorDaclState;
        PriorDaclBytesBase64 = priorDaclBytesBase64;
        PriorDaclSha256 = priorDaclSha256;
        PromotedJobId = promotedJobId;
        ProviderUniqueName = providerUniqueName;
        KeyStorageRoot = keyStorageRoot;
        DescriptorFormat = descriptorFormat;
    }

    public string EntryId { get; }

    public string InstallationOwnershipId { get; }

    public string OwningUserSid { get; }

    public string ServiceSid { get; }

    public ServiceOwnershipCredentialKind CredentialKind { get; }

    public string CertificateThumbprintSha1 { get; }

    public ServiceOwnershipProvenance Provenance { get; }

    public ServiceOwnershipPrivateKeyProviderKind PrivateKeyProviderKind { get; }

    public ServiceOwnershipRightsProfileId RightsProfileId { get; }

    public string KeyIdentity { get; }

    public ServiceOwnershipGrantMechanism GrantMechanism { get; }

    public ServiceOwnershipRightsMask GrantedRightsMask { get; }

    public int RightsPolicyVersion { get; }

    public ServiceOwnershipPriorDaclState PriorDaclState { get; }

    public string PriorDaclBytesBase64 { get; }

    public string PriorDaclSha256 { get; }

    public string PromotedJobId { get; }

    public string ProviderUniqueName { get; }

    public ServiceOwnershipKeyStorageRoot KeyStorageRoot { get; }

    public ServiceOwnershipDescriptorFormat DescriptorFormat { get; }

    public static bool TryCreate(
        string? entryId,
        string? installationOwnershipId,
        string? owningUserSid,
        string? serviceSid,
        ServiceOwnershipCredentialKind credentialKind,
        string? certificateThumbprintSha1,
        ServiceOwnershipProvenance provenance,
        ServiceOwnershipPrivateKeyProviderKind privateKeyProviderKind,
        ServiceOwnershipRightsProfileId rightsProfileId,
        string? keyIdentity,
        ServiceOwnershipGrantMechanism grantMechanism,
        ServiceOwnershipRightsMask grantedRightsMask,
        int rightsPolicyVersion,
        ServiceOwnershipPriorDaclState priorDaclState,
        string? priorDaclBytesBase64,
        string? priorDaclSha256,
        string? promotedJobId,
        string? providerUniqueName,
        ServiceOwnershipKeyStorageRoot keyStorageRoot,
        ServiceOwnershipDescriptorFormat descriptorFormat,
        out ServiceOwnershipTransitionFacts? facts)
    {
        facts = null;

        int max = ServiceOwnershipLedgerContract.MaxStringLength;
        if (!ServiceOwnershipLedgerContract.IsValidBoundedToken(entryId, max)
            || !ServiceOwnershipLedgerContract.IsValidBoundedToken(installationOwnershipId, max)
            || !ServiceOwnershipLedgerContract.IsValidBoundedToken(promotedJobId, max))
        {
            return false;
        }

        if (!ServiceOwnershipLedgerContract.IsValidKeyIdentity(keyIdentity)
            || !ServiceOwnershipLedgerContract.IsValidProviderUniqueName(providerUniqueName))
        {
            return false;
        }

        if (!IsBoundedSid(owningUserSid) || !IsBoundedSid(serviceSid))
        {
            return false;
        }

        if (!IsUppercaseSha1Hex(certificateThumbprintSha1))
        {
            return false;
        }

        // Zero is never a legal member of any of these closed vocabularies, and a
        // value outside the declared range would emit an empty wire token.
        if (credentialKind == ServiceOwnershipCredentialKind.Unspecified
            || provenance == ServiceOwnershipProvenance.Unspecified
            || privateKeyProviderKind == ServiceOwnershipPrivateKeyProviderKind.Unspecified
            || rightsProfileId == ServiceOwnershipRightsProfileId.Unspecified
            || grantMechanism == ServiceOwnershipGrantMechanism.Unspecified
            || priorDaclState == ServiceOwnershipPriorDaclState.Unspecified
            || keyStorageRoot == ServiceOwnershipKeyStorageRoot.Unspecified
            || descriptorFormat == ServiceOwnershipDescriptorFormat.Unspecified)
        {
            return false;
        }

        string priorBase64 = priorDaclBytesBase64 ?? string.Empty;
        string priorSha256 = priorDaclSha256 ?? string.Empty;

        if (priorDaclState is ServiceOwnershipPriorDaclState.Absent or ServiceOwnershipPriorDaclState.Empty)
        {
            if (priorBase64.Length != 0 || priorSha256.Length != 0)
            {
                return false;
            }
        }
        else
        {
            if (priorBase64.Length == 0
                || priorBase64.Length > ServiceOwnershipLedgerContract.MaxPriorDaclBase64Length
                || !IsBoundedBase64(priorBase64)
                || !ServiceOwnershipLedgerContract.IsUppercaseSha256Hex(priorSha256))
            {
                return false;
            }
        }

        if (rightsPolicyVersion <= 0)
        {
            return false;
        }

        facts = new ServiceOwnershipTransitionFacts(
            entryId!, installationOwnershipId!, owningUserSid!, serviceSid!, credentialKind,
            certificateThumbprintSha1!, provenance, privateKeyProviderKind, rightsProfileId,
            keyIdentity!, grantMechanism, grantedRightsMask, rightsPolicyVersion, priorDaclState,
            priorBase64, priorSha256, promotedJobId!, providerUniqueName!, keyStorageRoot,
            descriptorFormat);
        return true;
    }

    private static bool IsBoundedSid(string? sid)
    {
        if (sid is null || sid.Length < 3 || sid.Length > ServiceOwnershipLedgerContract.MaxSidLength)
        {
            return false;
        }
        foreach (char c in sid)
        {
            bool ok = (c >= '0' && c <= '9') || c == '-' || c == 'S' || c == 's';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsUppercaseSha1Hex(string? value)
    {
        if (value is null || value.Length != 40)
        {
            return false;
        }
        foreach (char c in value)
        {
            bool ok = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsBoundedBase64(string value)
    {
        foreach (char c in value)
        {
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                      || (c >= '0' && c <= '9') || c == '+' || c == '/' || c == '=';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// A closed transition request. The only constructor is PRIVATE, so an instance
/// exists only if <see cref="TryCreate"/> accepted the operation, the source
/// state and every bounded identifier.
/// </summary>
public sealed class ServiceOwnershipTransitionRequest
{
    private ServiceOwnershipTransitionRequest(
        ServiceOwnershipTransitionOperation operation,
        ServiceOwnershipLedgerValidationResult source,
        ServiceOwnershipTransitionFacts? facts,
        string targetEntryId,
        string targetPromotedJobId,
        string operationId,
        string utcTimestamp)
    {
        Operation = operation;
        Source = source;
        Facts = facts;
        TargetEntryId = targetEntryId;
        TargetPromotedJobId = targetPromotedJobId;
        OperationId = operationId;
        UtcTimestamp = utcTimestamp;
    }

    public ServiceOwnershipTransitionOperation Operation { get; }

    /// <summary>An ACCEPTED ledger result. A refused one can never reach here.</summary>
    public ServiceOwnershipLedgerValidationResult Source { get; }

    public ServiceOwnershipTransitionFacts? Facts { get; }

    public string TargetEntryId { get; }

    public string TargetPromotedJobId { get; }

    public string OperationId { get; }

    /// <summary>An RFC 3339 UTC instant supplied by the caller. Nothing here reads a clock.</summary>
    public string UtcTimestamp { get; }

    public static bool TryCreate(
        ServiceOwnershipTransitionOperation operation,
        ServiceOwnershipLedgerValidationResult? source,
        ServiceOwnershipTransitionFacts? facts,
        string? targetEntryId,
        string? targetPromotedJobId,
        string? operationId,
        string? utcTimestamp,
        out ServiceOwnershipTransitionRequest? request)
    {
        request = null;

        if (operation == ServiceOwnershipTransitionOperation.Unspecified
            || !Enum.IsDefined(typeof(ServiceOwnershipTransitionOperation), operation))
        {
            return false;
        }
        if (source is null || source.IsRefused)
        {
            return false;
        }
        if (!ServiceOwnershipLedgerContract.IsValidBoundedToken(
                operationId, ServiceOwnershipLedgerContract.MaxStringLength))
        {
            return false;
        }
        if (!IsCanonicalUtcTimestamp(utcTimestamp))
        {
            return false;
        }

        string entryId = targetEntryId ?? string.Empty;
        string jobId = targetPromotedJobId ?? string.Empty;

        if (entryId.Length != 0
            && !ServiceOwnershipLedgerContract.IsValidBoundedToken(
                entryId, ServiceOwnershipLedgerContract.MaxStringLength))
        {
            return false;
        }
        if (jobId.Length != 0
            && !ServiceOwnershipLedgerContract.IsValidBoundedToken(
                jobId, ServiceOwnershipLedgerContract.MaxStringLength))
        {
            return false;
        }

        request = new ServiceOwnershipTransitionRequest(
            operation, source, facts, entryId, jobId, operationId!, utcTimestamp!);
        return true;
    }

    /// <summary>
    /// The exact <c>yyyy-MM-ddTHH:mm:ssZ</c> spelling the ledger emits. A looser
    /// instant would round-trip to a different text and break byte equality.
    /// </summary>
    private static bool IsCanonicalUtcTimestamp(string? value)
    {
        if (value is null || value.Length != 20)
        {
            return false;
        }
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool expected = i switch
            {
                4 or 7 => c == '-',
                10 => c == 'T',
                13 or 16 => c == ':',
                19 => c == 'Z',
                _ => c >= '0' && c <= '9',
            };
            if (!expected)
            {
                return false;
            }
        }

        return DateTimeOffset.TryParseExact(
            value,
            "yyyy-MM-dd'T'HH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);
    }
}

/// <summary>
/// Bounded transition result. It carries the next accepted state only when every
/// one of the four proofs passed. No reason string, path, exception text or
/// partial document is representable.
/// </summary>
public sealed class ServiceOwnershipTransitionResult
{
    private ServiceOwnershipTransitionResult(
        ServiceOwnershipTransitionOutcome outcome,
        ServiceOwnershipLedgerValidationResult? accepted,
        ServiceOwnershipLedgerSerializationResult? serialized,
        ServiceOwnershipLedgerOutcome ledgerOutcome,
        int generation)
    {
        Outcome = outcome;
        Accepted = accepted;
        Serialized = serialized;
        LedgerOutcome = ledgerOutcome;
        Generation = generation;
    }

    public ServiceOwnershipTransitionOutcome Outcome { get; }

    /// <summary>The next accepted ledger state, or null when nothing was produced.</summary>
    public ServiceOwnershipLedgerValidationResult? Accepted { get; }

    /// <summary>The serialized next state, or null when nothing was produced.</summary>
    public ServiceOwnershipLedgerSerializationResult? Serialized { get; }

    /// <summary>The accepted ledger outcome of the next state.</summary>
    public ServiceOwnershipLedgerOutcome LedgerOutcome { get; }

    /// <summary>The generation of the next state, or zero when nothing was produced.</summary>
    public int Generation { get; }

    public bool IsTransitioned =>
        Outcome == ServiceOwnershipTransitionOutcome.Transitioned;

    /// <summary>Carries only the bounded outcome token.</summary>
    public override string ToString() => Outcome.ToString();

    internal static ServiceOwnershipTransitionResult Refused(
        ServiceOwnershipTransitionOutcome outcome) =>
        new(outcome, null, null, ServiceOwnershipLedgerOutcome.Malformed, 0);

    internal static ServiceOwnershipTransitionResult Transitioned(
        ServiceOwnershipLedgerValidationResult accepted,
        ServiceOwnershipLedgerSerializationResult serialized,
        ServiceOwnershipLedgerOutcome ledgerOutcome,
        int generation) =>
        new(ServiceOwnershipTransitionOutcome.Transitioned, accepted, serialized, ledgerOutcome, generation);
}

/// <summary>
/// The pure, deterministic ledger transition authority. It reads nothing, writes
/// nothing and throws nothing: every unusable input maps to a bounded refusal.
/// </summary>
public static class ServiceOwnershipTransitionAuthority
{
    public static ServiceOwnershipTransitionResult Apply(ServiceOwnershipTransitionRequest? request)
    {
        try
        {
            return ApplyCore(request);
        }
        catch (Exception)
        {
            return ServiceOwnershipTransitionResult.Refused(
                ServiceOwnershipTransitionOutcome.InvalidRequest);
        }
    }

    private static ServiceOwnershipTransitionResult ApplyCore(
        ServiceOwnershipTransitionRequest? request)
    {
        if (request is null)
        {
            return Refuse(ServiceOwnershipTransitionOutcome.InvalidRequest);
        }

        ServiceOwnershipLedgerValidationResult source = request.Source;
        if (source.IsRefused)
        {
            return Refuse(ServiceOwnershipTransitionOutcome.SourceNotAccepted);
        }

        if (!TryBuildNextState(request, out DocumentModel? next, out ServiceOwnershipTransitionOutcome refusal))
        {
            return Refuse(refusal);
        }

        ServiceOwnershipLedgerOutcome predicted = PredictOutcome(next!);

        if (!TryEmitDocument(next!, out string json, out ServiceOwnershipTransitionOutcome emitRefusal))
        {
            return Refuse(emitRefusal);
        }

        ServiceOwnershipLedgerValidationResult accepted = ServiceOwnershipLedgerValidator.Validate(json);
        if (accepted.IsRefused || accepted.Document is null)
        {
            return Refuse(ServiceOwnershipTransitionOutcome.ComposedDocumentRefused);
        }
        if (accepted.Outcome != predicted)
        {
            return Refuse(ServiceOwnershipTransitionOutcome.ComposedOutcomeMismatch);
        }
        if (accepted.Document.Generation != next!.Generation)
        {
            return Refuse(ServiceOwnershipTransitionOutcome.ComposedDocumentRefused);
        }

        ServiceOwnershipLedgerSerializationResult serialized =
            ServiceOwnershipLedgerSerializer.Serialize(accepted);
        if (!serialized.IsSerialized || serialized.Utf8Bytes.Length == 0)
        {
            return Refuse(ServiceOwnershipTransitionOutcome.SerializationRefused);
        }

        ServiceOwnershipLedgerValidationResult roundTrip =
            ServiceOwnershipLedgerValidator.Validate(serialized.Json);
        if (roundTrip.IsRefused
            || roundTrip.Document is null
            || roundTrip.Outcome != accepted.Outcome
            || roundTrip.Document.Generation != accepted.Document.Generation)
        {
            return Refuse(ServiceOwnershipTransitionOutcome.RoundTripRefused);
        }

        return ServiceOwnershipTransitionResult.Transitioned(
            accepted, serialized, accepted.Outcome, accepted.Document.Generation);
    }

    private static ServiceOwnershipTransitionResult Refuse(ServiceOwnershipTransitionOutcome outcome) =>
        ServiceOwnershipTransitionResult.Refused(outcome);

    // -----------------------------------------------------------------------
    // THE TRANSITIONS
    // -----------------------------------------------------------------------

    private static bool TryBuildNextState(
        ServiceOwnershipTransitionRequest request,
        out DocumentModel? next,
        out ServiceOwnershipTransitionOutcome refusal)
    {
        next = null;
        refusal = ServiceOwnershipTransitionOutcome.Unspecified;

        ServiceOwnershipLedgerOutcome sourceOutcome = request.Source.Outcome;
        ServiceOwnershipLedgerDocument? sourceDocument = request.Source.Document;
        string stamp = request.UtcTimestamp;

        if (request.Operation == ServiceOwnershipTransitionOperation.BeginPromotionIntent)
        {
            if (sourceOutcome is not (ServiceOwnershipLedgerOutcome.Absent
                or ServiceOwnershipLedgerOutcome.ValidEmpty))
            {
                refusal = ServiceOwnershipTransitionOutcome.SourceStateMismatch;
                return false;
            }
            if (request.Facts is null)
            {
                refusal = ServiceOwnershipTransitionOutcome.FactsUnusable;
                return false;
            }
            if (sourceDocument is not null && sourceDocument.Entries.Count != 0)
            {
                refusal = ServiceOwnershipTransitionOutcome.SourceStateMismatch;
                return false;
            }

            ServiceOwnershipTransitionFacts facts = request.Facts;

            if (sourceDocument is not null
                && !string.Equals(
                    sourceDocument.InstallationOwnershipId,
                    facts.InstallationOwnershipId,
                    StringComparison.Ordinal))
            {
                refusal = ServiceOwnershipTransitionOutcome.FactsUnusable;
                return false;
            }

            var document = new DocumentModel
            {
                InstallationOwnershipId = facts.InstallationOwnershipId,
                Generation = sourceDocument is null ? 1 : sourceDocument.Generation + 1,
                TransactionState = ServiceOwnershipTransactionState.Preparing,
                CreatedUtc = sourceDocument?.CreatedUtc ?? stamp,
                UpdatedUtc = stamp,
                LastOperationId = request.OperationId,
            };
            document.Entries.Add(NewEntry(facts, stamp));

            next = document;
            return true;
        }

        if (sourceDocument is null)
        {
            refusal = ServiceOwnershipTransitionOutcome.SourceNotAccepted;
            return false;
        }

        var model = FromDocument(sourceDocument);
        model.Generation = sourceDocument.Generation + 1;
        model.UpdatedUtc = stamp;
        model.LastOperationId = request.OperationId;

        int index = IndexOfEntry(model, request.TargetEntryId);

        switch (request.Operation)
        {
            case ServiceOwnershipTransitionOperation.RecordCredentialMutated:
                if (sourceDocument.TransactionState != ServiceOwnershipTransactionState.Preparing)
                {
                    refusal = ServiceOwnershipTransitionOutcome.SourceStateMismatch;
                    return false;
                }
                if (index < 0 || model.Entries[index].LifecycleState != ServiceOwnershipLifecycleState.Intended)
                {
                    refusal = ServiceOwnershipTransitionOutcome.TargetEntryUnusable;
                    return false;
                }
                model.TransactionState = ServiceOwnershipTransactionState.CredentialMutated;
                model.Entries[index].UpdatedUtc = stamp;
                break;

            case ServiceOwnershipTransitionOperation.CompletePromotion:
                if (sourceDocument.TransactionState != ServiceOwnershipTransactionState.CredentialMutated)
                {
                    refusal = ServiceOwnershipTransitionOutcome.SourceStateMismatch;
                    return false;
                }
                if (index < 0 || model.Entries[index].LifecycleState != ServiceOwnershipLifecycleState.Intended)
                {
                    refusal = ServiceOwnershipTransitionOutcome.TargetEntryUnusable;
                    return false;
                }
                model.TransactionState = ServiceOwnershipTransactionState.Done;
                model.Entries[index].LifecycleState = ServiceOwnershipLifecycleState.Active;
                model.Entries[index].UpdatedUtc = stamp;
                break;

            case ServiceOwnershipTransitionOperation.BeginRestore:
                if (index < 0 || model.Entries[index].LifecycleState != ServiceOwnershipLifecycleState.Active)
                {
                    refusal = ServiceOwnershipTransitionOutcome.TargetEntryUnusable;
                    return false;
                }
                model.TransactionState = ServiceOwnershipTransactionState.Restoring;
                model.Entries[index].LifecycleState = ServiceOwnershipLifecycleState.Restoring;
                model.Entries[index].UpdatedUtc = stamp;
                break;

            case ServiceOwnershipTransitionOperation.MarkRestored:
                if (sourceDocument.TransactionState != ServiceOwnershipTransactionState.Restoring)
                {
                    refusal = ServiceOwnershipTransitionOutcome.SourceStateMismatch;
                    return false;
                }
                if (index < 0 || model.Entries[index].LifecycleState != ServiceOwnershipLifecycleState.Restoring)
                {
                    refusal = ServiceOwnershipTransitionOutcome.TargetEntryUnusable;
                    return false;
                }
                model.TransactionState = ServiceOwnershipTransactionState.Done;
                model.Entries[index].LifecycleState = ServiceOwnershipLifecycleState.Restored;
                model.Entries[index].UpdatedUtc = stamp;
                break;

            case ServiceOwnershipTransitionOperation.RemoveRestoredEntry:
                if (index < 0 || model.Entries[index].LifecycleState != ServiceOwnershipLifecycleState.Restored)
                {
                    refusal = ServiceOwnershipTransitionOutcome.TargetEntryUnusable;
                    return false;
                }
                model.Entries.RemoveAt(index);
                model.TransactionState = model.Entries.Count == 0
                    ? ServiceOwnershipTransactionState.Idle
                    : ServiceOwnershipTransactionState.Done;
                break;

            case ServiceOwnershipTransitionOperation.RemoveAbandonedIntentEntry:
                if (index < 0 || model.Entries[index].LifecycleState != ServiceOwnershipLifecycleState.Intended)
                {
                    refusal = ServiceOwnershipTransitionOutcome.TargetEntryUnusable;
                    return false;
                }
                model.Entries.RemoveAt(index);
                model.TransactionState = model.Entries.Count == 0
                    ? ServiceOwnershipTransactionState.Idle
                    : ServiceOwnershipTransactionState.Done;
                break;

            case ServiceOwnershipTransitionOperation.RemoveJobAssociation:
                if (index < 0)
                {
                    refusal = ServiceOwnershipTransitionOutcome.TargetEntryUnusable;
                    return false;
                }
                if (request.TargetPromotedJobId.Length == 0
                    || !model.Entries[index].AssociatedPromotedJobIds.Remove(request.TargetPromotedJobId))
                {
                    refusal = ServiceOwnershipTransitionOutcome.TargetJobUnusable;
                    return false;
                }
                model.Entries[index].UpdatedUtc = stamp;
                break;

            case ServiceOwnershipTransitionOperation.MarkStale:
                if (index < 0)
                {
                    refusal = ServiceOwnershipTransitionOutcome.TargetEntryUnusable;
                    return false;
                }
                if (model.Entries[index].LifecycleState == ServiceOwnershipLifecycleState.Stale)
                {
                    refusal = ServiceOwnershipTransitionOutcome.TargetEntryUnusable;
                    return false;
                }

                // STALE PRESERVES EVERYTHING. The lifecycle token and the entry
                // timestamp are the ONLY fields that move; the transaction state is
                // carried forward untouched. Nothing is removed, nothing is
                // restored, and no completion is reported.
                model.Entries[index].LifecycleState = ServiceOwnershipLifecycleState.Stale;
                model.Entries[index].UpdatedUtc = stamp;
                break;

            default:
                refusal = ServiceOwnershipTransitionOutcome.InvalidRequest;
                return false;
        }

        next = model;
        return true;
    }

    private static int IndexOfEntry(DocumentModel model, string entryId)
    {
        if (entryId.Length == 0)
        {
            return -1;
        }
        for (int i = 0; i < model.Entries.Count; i++)
        {
            if (string.Equals(model.Entries[i].EntryId, entryId, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// The SAME rule the validator applies, stated independently so a disagreement
    /// between the two is detectable. The validator remains the decision authority;
    /// this only predicts, and a mismatch refuses the whole transition.
    /// </summary>
    private static ServiceOwnershipLedgerOutcome PredictOutcome(DocumentModel model)
    {
        if (model.Entries.Count == 0)
        {
            return ServiceOwnershipLedgerOutcome.ValidEmpty;
        }

        foreach (EntryModel entry in model.Entries)
        {
            if (entry.LifecycleState == ServiceOwnershipLifecycleState.Stale)
            {
                return ServiceOwnershipLedgerOutcome.Stale;
            }
        }

        bool allActive = true;
        foreach (EntryModel entry in model.Entries)
        {
            if (entry.LifecycleState != ServiceOwnershipLifecycleState.Active)
            {
                allActive = false;
                break;
            }
        }

        return model.TransactionState == ServiceOwnershipTransactionState.Done && allActive
            ? ServiceOwnershipLedgerOutcome.Active
            : ServiceOwnershipLedgerOutcome.InProgress;
    }

    // -----------------------------------------------------------------------
    // THE MODEL
    // -----------------------------------------------------------------------

    private sealed class DocumentModel
    {
        internal string InstallationOwnershipId = string.Empty;
        internal int Generation;
        internal ServiceOwnershipTransactionState TransactionState = ServiceOwnershipTransactionState.Unspecified;
        internal string CreatedUtc = string.Empty;
        internal string UpdatedUtc = string.Empty;
        internal string LastOperationId = string.Empty;
        internal readonly List<EntryModel> Entries = new();
    }

    private sealed class EntryModel
    {
        internal string EntryId = string.Empty;
        internal string OwningUserSid = string.Empty;
        internal string ServiceSid = string.Empty;
        internal ServiceOwnershipCredentialKind CredentialKind;
        internal string CertificateThumbprintSha1 = string.Empty;
        internal ServiceOwnershipProvenance Provenance;
        internal ServiceOwnershipPrivateKeyProviderKind PrivateKeyProviderKind;
        internal ServiceOwnershipRightsProfileId RightsProfileId;
        internal string KeyIdentity = string.Empty;
        internal ServiceOwnershipGrantMechanism GrantMechanism;
        internal ServiceOwnershipRightsMask GrantedRightsMask;
        internal int RightsPolicyVersion;
        internal ServiceOwnershipPriorDaclState PriorDaclState;
        internal string PriorDaclBytesBase64 = string.Empty;
        internal string PriorDaclSha256 = string.Empty;
        internal readonly List<string> AssociatedPromotedJobIds = new();
        internal ServiceOwnershipLifecycleState LifecycleState;
        internal string CreatedUtc = string.Empty;
        internal string UpdatedUtc = string.Empty;
        internal string ProviderUniqueName = string.Empty;
        internal ServiceOwnershipKeyStorageRoot KeyStorageRoot;
        internal ServiceOwnershipDescriptorFormat DescriptorFormat;
    }

    /// <summary>
    /// Copies EVERY captured fact off an accepted entry. capturedStateBindingSha256
    /// is deliberately NOT copied: it is a function of the generation and is
    /// recomputed at emit time from the fields carried here.
    /// </summary>
    private static DocumentModel FromDocument(ServiceOwnershipLedgerDocument document)
    {
        var model = new DocumentModel
        {
            InstallationOwnershipId = document.InstallationOwnershipId,
            Generation = document.Generation,
            TransactionState = document.TransactionState,
            CreatedUtc = document.CreatedUtc,
            UpdatedUtc = document.UpdatedUtc,
            LastOperationId = document.LastOperationId,
        };

        foreach (ServiceOwnershipLedgerEntry entry in document.Entries)
        {
            var copy = new EntryModel
            {
                EntryId = entry.EntryId,
                OwningUserSid = entry.OwningUserSid,
                ServiceSid = entry.ServiceSid,
                CredentialKind = entry.CredentialKind,
                CertificateThumbprintSha1 = entry.CertificateThumbprintSha1,
                Provenance = entry.Provenance,
                PrivateKeyProviderKind = entry.PrivateKeyProviderKind,
                RightsProfileId = entry.RightsProfileId,
                KeyIdentity = entry.KeyIdentity,
                GrantMechanism = entry.GrantMechanism,
                GrantedRightsMask = entry.GrantedRightsMask,
                RightsPolicyVersion = entry.RightsPolicyVersion,
                PriorDaclState = entry.PriorDaclState,
                PriorDaclBytesBase64 = entry.PriorDaclBytesBase64,
                PriorDaclSha256 = entry.PriorDaclSha256,
                LifecycleState = entry.LifecycleState,
                CreatedUtc = entry.CreatedUtc,
                UpdatedUtc = entry.UpdatedUtc,
                ProviderUniqueName = entry.ProviderUniqueName,
                KeyStorageRoot = entry.KeyStorageRoot,
                DescriptorFormat = entry.DescriptorFormat,
            };
            copy.AssociatedPromotedJobIds.AddRange(entry.AssociatedPromotedJobIds);
            model.Entries.Add(copy);
        }

        return model;
    }

    private static EntryModel NewEntry(ServiceOwnershipTransitionFacts facts, string stamp)
    {
        var entry = new EntryModel
        {
            EntryId = facts.EntryId,
            OwningUserSid = facts.OwningUserSid,
            ServiceSid = facts.ServiceSid,
            CredentialKind = facts.CredentialKind,
            CertificateThumbprintSha1 = facts.CertificateThumbprintSha1,
            Provenance = facts.Provenance,
            PrivateKeyProviderKind = facts.PrivateKeyProviderKind,
            RightsProfileId = facts.RightsProfileId,
            KeyIdentity = facts.KeyIdentity,
            GrantMechanism = facts.GrantMechanism,
            GrantedRightsMask = facts.GrantedRightsMask,
            RightsPolicyVersion = facts.RightsPolicyVersion,
            PriorDaclState = facts.PriorDaclState,
            PriorDaclBytesBase64 = facts.PriorDaclBytesBase64,
            PriorDaclSha256 = facts.PriorDaclSha256,
            LifecycleState = ServiceOwnershipLifecycleState.Intended,
            CreatedUtc = stamp,
            UpdatedUtc = stamp,
            ProviderUniqueName = facts.ProviderUniqueName,
            KeyStorageRoot = facts.KeyStorageRoot,
            DescriptorFormat = facts.DescriptorFormat,
        };
        entry.AssociatedPromotedJobIds.Add(facts.PromotedJobId);
        return entry;
    }

    // -----------------------------------------------------------------------
    // THE EMITTER - driven by the parser's OWN closed property lists
    // -----------------------------------------------------------------------

    private static bool TryEmitDocument(
        DocumentModel model, out string json, out ServiceOwnershipTransitionOutcome refusal)
    {
        json = string.Empty;
        refusal = ServiceOwnershipTransitionOutcome.Unspecified;

        var sb = new StringBuilder("{");
        bool first = true;

        foreach (string name in ServiceOwnershipLedgerContract.DocumentPropertyNames)
        {
            string raw;
            switch (name)
            {
                case "schemaVersion":
                    raw = ServiceOwnershipLedgerContract.LedgerSchemaVersion
                        .ToString(CultureInfo.InvariantCulture);
                    break;
                case "productOwnershipMarker":
                    if (!TryQuote(ServiceOwnershipLedgerContract.ProductOwnershipMarker, out raw))
                    {
                        refusal = ServiceOwnershipTransitionOutcome.UnserializableValue;
                        return false;
                    }
                    break;
                case "managedFeatureId":
                    if (!TryQuote(ServiceOwnershipLedgerContract.ManagedFeatureId, out raw))
                    {
                        refusal = ServiceOwnershipTransitionOutcome.UnserializableValue;
                        return false;
                    }
                    break;
                case "installationOwnershipId":
                    if (!TryQuote(model.InstallationOwnershipId, out raw))
                    {
                        refusal = ServiceOwnershipTransitionOutcome.UnserializableValue;
                        return false;
                    }
                    break;
                case "generation":
                    raw = model.Generation.ToString(CultureInfo.InvariantCulture);
                    break;
                case "transactionState":
                    if (!TryQuote(
                            ServiceOwnershipLedgerContract.ToWireToken(model.TransactionState), out raw))
                    {
                        refusal = ServiceOwnershipTransitionOutcome.UnserializableValue;
                        return false;
                    }
                    break;
                case "entries":
                    if (!TryEmitEntries(model, out raw, out refusal))
                    {
                        return false;
                    }
                    break;
                case "createdUtc":
                    if (!TryQuote(model.CreatedUtc, out raw))
                    {
                        refusal = ServiceOwnershipTransitionOutcome.UnserializableValue;
                        return false;
                    }
                    break;
                case "updatedUtc":
                    if (!TryQuote(model.UpdatedUtc, out raw))
                    {
                        refusal = ServiceOwnershipTransitionOutcome.UnserializableValue;
                        return false;
                    }
                    break;
                case "lastOperationId":
                    if (!TryQuote(model.LastOperationId, out raw))
                    {
                        refusal = ServiceOwnershipTransitionOutcome.UnserializableValue;
                        return false;
                    }
                    break;
                default:
                    // A schema property the parser knows and this emitter does not.
                    refusal = ServiceOwnershipTransitionOutcome.UnmappedSchemaProperty;
                    return false;
            }

            if (!first)
            {
                sb.Append(',');
            }
            first = false;
            if (!TryQuote(name, out string quotedName))
            {
                refusal = ServiceOwnershipTransitionOutcome.UnserializableValue;
                return false;
            }
            sb.Append(quotedName).Append(':').Append(raw);
        }

        json = sb.Append('}').ToString();
        return true;
    }

    private static bool TryEmitEntries(
        DocumentModel model, out string raw, out ServiceOwnershipTransitionOutcome refusal)
    {
        raw = string.Empty;
        refusal = ServiceOwnershipTransitionOutcome.Unspecified;

        var sb = new StringBuilder("[");
        for (int i = 0; i < model.Entries.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            if (!TryEmitEntry(model, model.Entries[i], out string entryJson, out refusal))
            {
                return false;
            }
            sb.Append(entryJson);
        }

        raw = sb.Append(']').ToString();
        return true;
    }

    private static bool TryEmitEntry(
        DocumentModel model,
        EntryModel entry,
        out string json,
        out ServiceOwnershipTransitionOutcome refusal)
    {
        json = string.Empty;
        refusal = ServiceOwnershipTransitionOutcome.Unspecified;

        string maskText = entry.GrantedRightsMask.ToWireText();

        // THE BINDING IS RECOMPUTED, NEVER CARRIED. It is a function of the
        // generation, so carrying a prior value forward would make every
        // generation bump produce an entry the validator refuses.
        string binding = ServiceOwnershipLedgerContract.ComputeCapturedStateBinding(
            model.Generation,
            entry.OwningUserSid,
            entry.ServiceSid,
            entry.CertificateThumbprintSha1,
            entry.PrivateKeyProviderKind,
            entry.RightsProfileId,
            entry.KeyIdentity,
            entry.GrantMechanism,
            maskText,
            entry.PriorDaclState,
            entry.PriorDaclSha256,
            entry.ProviderUniqueName,
            entry.KeyStorageRoot,
            entry.DescriptorFormat);

        var sb = new StringBuilder("{");
        bool first = true;

        foreach (string name in ServiceOwnershipLedgerContract.EntryPropertyNames)
        {
            string raw;
            switch (name)
            {
                case "entryId":
                    if (!TryQuote(entry.EntryId, out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "owningUserSid":
                    if (!TryQuote(entry.OwningUserSid, out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "serviceSid":
                    if (!TryQuote(entry.ServiceSid, out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "credentialKind":
                    if (!TryQuote(ServiceOwnershipLedgerContract.ToWireToken(entry.CredentialKind), out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "certificateThumbprintSha1":
                    if (!TryQuote(entry.CertificateThumbprintSha1, out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "provenance":
                    if (!TryQuote(ServiceOwnershipLedgerContract.ToWireToken(entry.Provenance), out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "privateKeyProviderKind":
                    if (!TryQuote(ServiceOwnershipLedgerContract.ToWireToken(entry.PrivateKeyProviderKind), out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "rightsProfileId":
                    if (!TryQuote(ServiceOwnershipLedgerContract.ToWireToken(entry.RightsProfileId), out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "keyIdentity":
                    if (!TryQuote(entry.KeyIdentity, out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "grantMechanism":
                    if (!TryQuote(ServiceOwnershipLedgerContract.ToWireToken(entry.GrantMechanism), out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "grantedRightsMask":
                    if (!TryQuote(maskText, out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "rightsPolicyVersion":
                    raw = entry.RightsPolicyVersion.ToString(CultureInfo.InvariantCulture);
                    break;
                case "priorDaclState":
                    if (!TryQuote(ServiceOwnershipLedgerContract.ToWireToken(entry.PriorDaclState), out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "priorDaclBytesBase64":
                    if (!TryQuote(entry.PriorDaclBytesBase64, out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "priorDaclSha256":
                    if (!TryQuote(entry.PriorDaclSha256, out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "capturedStateBindingSha256":
                    if (!TryQuote(binding, out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "associatedPromotedJobIds":
                    if (!TryEmitJobIds(entry, out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "lifecycleState":
                    if (!TryQuote(ServiceOwnershipLedgerContract.ToWireToken(entry.LifecycleState), out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "createdUtc":
                    if (!TryQuote(entry.CreatedUtc, out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "updatedUtc":
                    if (!TryQuote(entry.UpdatedUtc, out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "providerUniqueName":
                    if (!TryQuote(entry.ProviderUniqueName, out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "keyStorageRoot":
                    if (!TryQuote(ServiceOwnershipLedgerContract.ToWireToken(entry.KeyStorageRoot), out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                case "descriptorFormat":
                    if (!TryQuote(ServiceOwnershipLedgerContract.ToWireToken(entry.DescriptorFormat), out raw)) { refusal = ServiceOwnershipTransitionOutcome.UnserializableValue; return false; }
                    break;
                default:
                    refusal = ServiceOwnershipTransitionOutcome.UnmappedSchemaProperty;
                    return false;
            }

            if (!first)
            {
                sb.Append(',');
            }
            first = false;
            if (!TryQuote(name, out string quotedName))
            {
                refusal = ServiceOwnershipTransitionOutcome.UnserializableValue;
                return false;
            }
            sb.Append(quotedName).Append(':').Append(raw);
        }

        json = sb.Append('}').ToString();
        return true;
    }

    private static bool TryEmitJobIds(EntryModel entry, out string raw)
    {
        raw = string.Empty;
        var sb = new StringBuilder("[");
        for (int i = 0; i < entry.AssociatedPromotedJobIds.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }
            if (!TryQuote(entry.AssociatedPromotedJobIds[i], out string quoted))
            {
                return false;
            }
            sb.Append(quoted);
        }
        raw = sb.Append(']').ToString();
        return true;
    }

    /// <summary>
    /// Emits a JSON string literal WITHOUT escaping. A value that would need an
    /// escape is refused instead, so nothing the parser would have rejected on the
    /// way in can ride out through an escaped value on the way out.
    /// </summary>
    private static bool TryQuote(string? value, out string quoted)
    {
        quoted = string.Empty;
        if (value is null)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c < 0x20 || c > 0x7E || c == '"' || c == '\\')
            {
                return false;
            }
        }

        quoted = "\"" + value + "\"";
        return true;
    }
}
