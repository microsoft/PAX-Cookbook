using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PAXCookbook.Shared.Contracts;

// ---------------------------------------------------------------------------
// SERVICE OWNERSHIP LEDGER SERIALIZER - schema v3, cycle 82 (D3).
//
// WHAT THIS IS. The FIRST and ONLY writer for the service ownership ledger. It
// is a pure, portable, deterministic function from an ALREADY-VALIDATED ledger
// result to the exact UTF-8 bytes of an equivalent schema-v3 document.
//
// WHY IT TAKES A VALIDATION RESULT AND NOT A DOCUMENT. ServiceOwnershipLedgerDocument
// has an INTERNAL constructor, so the only way to hold one is to have obtained it
// from ServiceOwnershipLedgerValidator. Taking the RESULT rather than the document
// keeps that property visible at the signature: a caller cannot assemble an
// arbitrary model and ask for it to be written out as accepted state. There is no
// overload that accepts loose fields, and there is no way to widen this at run
// time - no environment variable, switch, file or machine key changes what it
// emits.
//
// WHAT THIS IS NOT. It opens no file, composes no path, reads no environment
// variable, touches no certificate store or private key, reads or writes no ACL,
// reads no registry, starts no process, and performs no I/O of any kind. It
// returns bytes; something else, elsewhere, would have to decide where they go.
// NOTHING IN THE PRODUCT CALLS IT.
//
// THE PROPERTY ORDER IS MIRRORED, NOT RETYPED. Both writers below iterate the
// parser's OWN closed property lists - ServiceOwnershipLedgerContract
// .DocumentPropertyNames and .EntryPropertyNames - and switch on each name. A
// schema field added to the parser without being added here does not silently
// vanish from the output: it lands on the default arm and the whole serialization
// is REFUSED. That is the point. A silently dropped priorDaclBytesBase64 would be
// an unrecoverable private-key ACL.
//
// NO UNBOUNDED VALUE CAN ESCAPE. Every emitted string is checked against the
// bounded charset the parser accepts. A value that would require JSON escaping -
// a quote, a backslash, a control character, anything non-ASCII - is refused
// rather than escaped, so no exception text, path, message or hostile payload can
// ride out through a value the parser would have refused on the way in.
//
// SELF-CHECK. Every successful serialization is re-parsed through the validator
// before it is returned. If the round trip is not accepted, or does not report
// the SAME outcome as the source, nothing is emitted.
// ---------------------------------------------------------------------------

/// <summary>Bounded serialization outcome. Zero always refuses.</summary>
public enum ServiceOwnershipLedgerSerializationOutcome
{
    Unspecified = 0,

    /// <summary>The document was written and survived its own round-trip self-check.</summary>
    Serialized = 1,

    /// <summary>The source result was null, refused, or carried no document.</summary>
    SourceNotAccepted = 2,

    /// <summary>A schema field has no writer here, so the output would be incomplete.</summary>
    UnmappedSchemaProperty = 3,

    /// <summary>A value is outside the bounded vocabulary the parser accepts.</summary>
    UnserializableValue = 4,

    /// <summary>The emitted bytes did not re-validate as an accepted document.</summary>
    RoundTripRefused = 5,

    /// <summary>The emitted bytes re-validated, but to a DIFFERENT outcome.</summary>
    RoundTripOutcomeMismatch = 6,
}

/// <summary>
/// Bounded serialization result. It carries JSON only when the serialization
/// succeeded AND survived its own round-trip self-check. No reason string, path,
/// exception text or partial document is representable.
/// </summary>
public sealed class ServiceOwnershipLedgerSerializationResult
{
    private static readonly byte[] NoBytes = Array.Empty<byte>();

    private ServiceOwnershipLedgerSerializationResult(
        ServiceOwnershipLedgerSerializationOutcome outcome,
        string json,
        byte[] utf8Bytes)
    {
        Outcome = outcome;
        Json = json;
        Utf8Bytes = utf8Bytes;
    }

    public ServiceOwnershipLedgerSerializationOutcome Outcome { get; }

    /// <summary>The emitted document, or the empty string when nothing was emitted.</summary>
    public string Json { get; }

    /// <summary>
    /// The emitted document's UTF-8 bytes, NEVER prefixed with a byte-order mark.
    /// A fresh copy is returned each time so a caller cannot mutate the result.
    /// </summary>
    public byte[] Utf8Bytes { get; }

    public bool IsSerialized =>
        Outcome == ServiceOwnershipLedgerSerializationOutcome.Serialized;

    internal static ServiceOwnershipLedgerSerializationResult Refused(
        ServiceOwnershipLedgerSerializationOutcome outcome) =>
        new(outcome, string.Empty, NoBytes);

    internal static ServiceOwnershipLedgerSerializationResult Success(string json, byte[] utf8Bytes) =>
        new(ServiceOwnershipLedgerSerializationOutcome.Serialized, json, utf8Bytes);
}

/// <summary>
/// The pure, deterministic schema-v3 ledger writer. It reads nothing, writes no
/// file and throws nothing: every unusable input maps to a bounded refusal.
/// </summary>
public static class ServiceOwnershipLedgerSerializer
{
    // Strict UTF-8, encoderShouldEmitUTF8Identifier FALSE. The reader
    // (ServiceOwnershipLedgerReaderInterpreter) treats a BOM as an unavailable
    // ledger, so emitting one would produce a file the product could not read.
    private static readonly UTF8Encoding Utf8NoBom =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Serializes an accepted ledger result to schema-v3 JSON. Absent results carry
    /// no document and are refused: "no ledger file" is not a document to write.
    /// </summary>
    public static ServiceOwnershipLedgerSerializationResult Serialize(
        ServiceOwnershipLedgerValidationResult? source)
    {
        try
        {
            if (source is null || source.IsRefused || source.Document is null)
            {
                return ServiceOwnershipLedgerSerializationResult.Refused(
                    ServiceOwnershipLedgerSerializationOutcome.SourceNotAccepted);
            }

            ServiceOwnershipLedgerDocument document = source.Document;
            if (document.SchemaVersion != ServiceOwnershipLedgerContract.LedgerSchemaVersion)
            {
                return ServiceOwnershipLedgerSerializationResult.Refused(
                    ServiceOwnershipLedgerSerializationOutcome.SourceNotAccepted);
            }

            var sb = new StringBuilder();
            ServiceOwnershipLedgerSerializationOutcome writeOutcome = WriteDocument(sb, document);
            if (writeOutcome != ServiceOwnershipLedgerSerializationOutcome.Serialized)
            {
                return ServiceOwnershipLedgerSerializationResult.Refused(writeOutcome);
            }

            string json = sb.ToString();

            // THE SELF-CHECK. The only honest correctness gate for a writer whose
            // reader is the validator is the validator itself.
            ServiceOwnershipLedgerValidationResult reparsed =
                ServiceOwnershipLedgerValidator.Validate(json);
            if (reparsed.IsRefused || reparsed.Document is null)
            {
                return ServiceOwnershipLedgerSerializationResult.Refused(
                    ServiceOwnershipLedgerSerializationOutcome.RoundTripRefused);
            }
            if (reparsed.Outcome != source.Outcome)
            {
                return ServiceOwnershipLedgerSerializationResult.Refused(
                    ServiceOwnershipLedgerSerializationOutcome.RoundTripOutcomeMismatch);
            }

            return ServiceOwnershipLedgerSerializationResult.Success(json, Utf8NoBom.GetBytes(json));
        }
        catch (Exception)
        {
            // Fail closed, exactly like the validator: no exception text escapes.
            return ServiceOwnershipLedgerSerializationResult.Refused(
                ServiceOwnershipLedgerSerializationOutcome.UnserializableValue);
        }
    }

    private static ServiceOwnershipLedgerSerializationOutcome WriteDocument(
        StringBuilder sb, ServiceOwnershipLedgerDocument document)
    {
        // STABLE ORDINAL ENTRY ORDERING. Entry ids are unique (the parser refuses
        // duplicates), so an ordinal sort is a total order and two documents with
        // the same entries always serialize identically.
        var entries = new List<ServiceOwnershipLedgerEntry>(document.Entries);
        entries.Sort(static (left, right) =>
            string.CompareOrdinal(left.EntryId, right.EntryId));

        sb.Append('{');
        bool first = true;

        foreach (string name in ServiceOwnershipLedgerContract.DocumentPropertyNames)
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;

            switch (name)
            {
                case "schemaVersion":
                    AppendName(sb, name);
                    sb.Append(document.SchemaVersion.ToString(CultureInfo.InvariantCulture));
                    break;

                case "productOwnershipMarker":
                    if (!AppendStringProperty(sb, name, document.ProductOwnershipMarker))
                    {
                        return ServiceOwnershipLedgerSerializationOutcome.UnserializableValue;
                    }
                    break;

                case "managedFeatureId":
                    if (!AppendStringProperty(sb, name, document.ManagedFeatureId))
                    {
                        return ServiceOwnershipLedgerSerializationOutcome.UnserializableValue;
                    }
                    break;

                case "installationOwnershipId":
                    if (!AppendStringProperty(sb, name, document.InstallationOwnershipId))
                    {
                        return ServiceOwnershipLedgerSerializationOutcome.UnserializableValue;
                    }
                    break;

                case "generation":
                    AppendName(sb, name);
                    sb.Append(document.Generation.ToString(CultureInfo.InvariantCulture));
                    break;

                case "transactionState":
                    if (!AppendTokenProperty(
                            sb, name,
                            ServiceOwnershipLedgerContract.ToWireToken(document.TransactionState)))
                    {
                        return ServiceOwnershipLedgerSerializationOutcome.UnserializableValue;
                    }
                    break;

                case "entries":
                    AppendName(sb, name);
                    sb.Append('[');
                    for (int i = 0; i < entries.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(',');
                        }
                        ServiceOwnershipLedgerSerializationOutcome entryOutcome =
                            WriteEntry(sb, entries[i]);
                        if (entryOutcome != ServiceOwnershipLedgerSerializationOutcome.Serialized)
                        {
                            return entryOutcome;
                        }
                    }
                    sb.Append(']');
                    break;

                case "createdUtc":
                    if (!AppendStringProperty(sb, name, document.CreatedUtc))
                    {
                        return ServiceOwnershipLedgerSerializationOutcome.UnserializableValue;
                    }
                    break;

                case "updatedUtc":
                    if (!AppendStringProperty(sb, name, document.UpdatedUtc))
                    {
                        return ServiceOwnershipLedgerSerializationOutcome.UnserializableValue;
                    }
                    break;

                case "lastOperationId":
                    if (!AppendStringProperty(sb, name, document.LastOperationId))
                    {
                        return ServiceOwnershipLedgerSerializationOutcome.UnserializableValue;
                    }
                    break;

                default:
                    // A schema property the parser knows and this writer does not.
                    return ServiceOwnershipLedgerSerializationOutcome.UnmappedSchemaProperty;
            }
        }

        sb.Append('}');
        return ServiceOwnershipLedgerSerializationOutcome.Serialized;
    }

    private static ServiceOwnershipLedgerSerializationOutcome WriteEntry(
        StringBuilder sb, ServiceOwnershipLedgerEntry entry)
    {
        // STABLE ORDINAL PROMOTED-JOB ORDERING, for the same reason as entries:
        // the parser refuses duplicate job ids, so this is a total order.
        var jobIds = new List<string>(entry.AssociatedPromotedJobIds);
        jobIds.Sort(StringComparer.Ordinal);

        sb.Append('{');
        bool first = true;

        foreach (string name in ServiceOwnershipLedgerContract.EntryPropertyNames)
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;

            bool ok;
            switch (name)
            {
                case "entryId":
                    ok = AppendStringProperty(sb, name, entry.EntryId);
                    break;
                case "owningUserSid":
                    ok = AppendStringProperty(sb, name, entry.OwningUserSid);
                    break;
                case "serviceSid":
                    ok = AppendStringProperty(sb, name, entry.ServiceSid);
                    break;
                case "credentialKind":
                    ok = AppendTokenProperty(
                        sb, name, ServiceOwnershipLedgerContract.ToWireToken(entry.CredentialKind));
                    break;
                case "certificateThumbprintSha1":
                    ok = AppendStringProperty(sb, name, entry.CertificateThumbprintSha1);
                    break;
                case "provenance":
                    ok = AppendTokenProperty(
                        sb, name, ServiceOwnershipLedgerContract.ToWireToken(entry.Provenance));
                    break;
                case "privateKeyProviderKind":
                    ok = AppendTokenProperty(
                        sb, name, ServiceOwnershipLedgerContract.ToWireToken(entry.PrivateKeyProviderKind));
                    break;
                case "rightsProfileId":
                    ok = AppendTokenProperty(
                        sb, name, ServiceOwnershipLedgerContract.ToWireToken(entry.RightsProfileId));
                    break;
                case "keyIdentity":
                    ok = AppendStringProperty(sb, name, entry.KeyIdentity);
                    break;
                case "grantMechanism":
                    ok = AppendTokenProperty(
                        sb, name, ServiceOwnershipLedgerContract.ToWireToken(entry.GrantMechanism));
                    break;
                case "grantedRightsMask":
                    ok = AppendStringProperty(sb, name, entry.GrantedRightsMask.ToWireText());
                    break;
                case "rightsPolicyVersion":
                    AppendName(sb, name);
                    sb.Append(entry.RightsPolicyVersion.ToString(CultureInfo.InvariantCulture));
                    ok = true;
                    break;
                case "priorDaclState":
                    ok = AppendTokenProperty(
                        sb, name, ServiceOwnershipLedgerContract.ToWireToken(entry.PriorDaclState));
                    break;
                case "priorDaclBytesBase64":
                    // MAY be legitimately empty (absent/empty prior state); may never
                    // be dropped, because these are the bytes that unwind the grant.
                    ok = AppendStringProperty(sb, name, entry.PriorDaclBytesBase64, allowEmpty: true);
                    break;
                case "priorDaclSha256":
                    ok = AppendStringProperty(sb, name, entry.PriorDaclSha256, allowEmpty: true);
                    break;
                case "capturedStateBindingSha256":
                    ok = AppendStringProperty(sb, name, entry.CapturedStateBindingSha256);
                    break;
                case "associatedPromotedJobIds":
                    AppendName(sb, name);
                    sb.Append('[');
                    ok = true;
                    for (int i = 0; i < jobIds.Count && ok; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(',');
                        }
                        ok = AppendStringValue(sb, jobIds[i]);
                    }
                    sb.Append(']');
                    break;
                case "lifecycleState":
                    ok = AppendTokenProperty(
                        sb, name, ServiceOwnershipLedgerContract.ToWireToken(entry.LifecycleState));
                    break;
                case "createdUtc":
                    ok = AppendStringProperty(sb, name, entry.CreatedUtc);
                    break;
                case "updatedUtc":
                    ok = AppendStringProperty(sb, name, entry.UpdatedUtc);
                    break;
                case "providerUniqueName":
                    ok = AppendStringProperty(sb, name, entry.ProviderUniqueName);
                    break;
                case "keyStorageRoot":
                    ok = AppendTokenProperty(
                        sb, name, ServiceOwnershipLedgerContract.ToWireToken(entry.KeyStorageRoot));
                    break;
                case "descriptorFormat":
                    ok = AppendTokenProperty(
                        sb, name, ServiceOwnershipLedgerContract.ToWireToken(entry.DescriptorFormat));
                    break;
                default:
                    return ServiceOwnershipLedgerSerializationOutcome.UnmappedSchemaProperty;
            }

            if (!ok)
            {
                return ServiceOwnershipLedgerSerializationOutcome.UnserializableValue;
            }
        }

        sb.Append('}');
        return ServiceOwnershipLedgerSerializationOutcome.Serialized;
    }

    private static void AppendName(StringBuilder sb, string name)
    {
        sb.Append('"').Append(name).Append('"').Append(':');
    }

    /// <summary>
    /// A wire token. The zero enum value has no token at all, so an empty token
    /// means the model held an Unspecified value and must never be written.
    /// </summary>
    private static bool AppendTokenProperty(StringBuilder sb, string name, string token)
    {
        if (token.Length == 0)
        {
            return false;
        }
        return AppendStringProperty(sb, name, token);
    }

    private static bool AppendStringProperty(
        StringBuilder sb, string name, string? value, bool allowEmpty = false)
    {
        if (value is null || (value.Length == 0 && !allowEmpty))
        {
            return false;
        }
        AppendName(sb, name);
        return AppendStringValue(sb, value);
    }

    /// <summary>
    /// Emits a JSON string WITHOUT ESCAPING, and refuses anything that would need
    /// escaping. Every value the parser accepts is drawn from a bounded charset, so
    /// a value requiring an escape is by definition a value the parser would have
    /// refused - not something to quietly encode on the way out.
    /// </summary>
    private static bool AppendStringValue(StringBuilder sb, string? value)
    {
        if (value is null)
        {
            return false;
        }
        foreach (char c in value)
        {
            bool safe = c is >= ' ' and <= '~' && c != '"' && c != '\\';
            if (!safe)
            {
                return false;
            }
        }
        sb.Append('"').Append(value).Append('"');
        return true;
    }
}
