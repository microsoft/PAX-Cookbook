using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PAXCookbook.Shared.Contracts;

// ---------------------------------------------------------------------------
// SERVICE INSTALLATION ANCHOR - schema v1 (cycle 58).
//
// WHAT THIS IS. A pure, portable, deterministic schema, validator and
// serializer for the ONE machine record that answers "which installation is
// this, and which NON-ELEVATED user initiated it". It is six properties: a
// schema version, a product marker, a feature id, an immutable installation
// id, the kernel-derived initiating-user SID, and a creation timestamp.
//
// WHAT THIS IS NOT. It is NOT the ownership ledger and must never be confused
// with it. ServiceOwnershipLedgerContract describes credential-permission
// ownership entries and lives in ownership-ledger.json; this contract
// describes installation identity and lives in installation-anchor.json. The
// two schemas are DISJOINT, neither is derived from the other, each validator
// REFUSES the other's document, and this contract deliberately does not extend
// or reuse the ledger's shape. Do not merge them.
//
// WHAT THIS FILE CANNOT DO, by construction. It performs NO file access, opens
// no handle, reads no environment variable, composes no path, generates no
// identifier, reads no token, resolves no account, elevates nothing, opens no
// socket, starts no process, touches no PAX and starts no Bake. It validates
// and emits strings. All identifier GENERATION and all I/O belong to the
// Setup-owned store.
//
// PORTABILITY. Portable framework namespaces only - no System.Security.*, no
// System.Environment.SpecialFolder, no Windows-only type - so this file stays
// safe to compile-link into the net8.0 (not net8.0-windows) Service assembly
// if a later cycle ever needs it. It is deliberately SELF-CONTAINED: it takes
// no dependency on any other contract type in this assembly.
//
// THE FILE NAME LIVES ELSEWHERE, ON PURPOSE. This contract declares NO file
// name and NO folder name. ServiceMachineStorageContract is the single name
// authority for machine storage, and it declares
// InstallationAnchorFileName alongside the existing folder and ledger names.
//
// NO ACCOUNT NAMES, EVER. The anchor records a SID STRING only. There is no
// property for a UPN, display name, sAMAccountName, domain, e-mail, token,
// claim or secret, and every one of those spellings is in the PROHIBITED
// property set, so a document carrying one fails the WHOLE anchor rather than
// being silently trimmed.
//
// WHY THE SID GRAMMAR IS NOT ServiceOwnershipLedgerContract.IsUserOwnerSid.
// That predicate requires identifier authority 5 with domain prefix 21, which
// is the classic S-1-5-21-a-b-c-rid shape. An Entra-joined machine hands out
// initiating-user SIDs in the S-1-12-1-... family, which has no account-domain
// SID at all and would be refused by that predicate. Refusing the initiating
// user of an Entra-joined installation would be a false negative on exactly
// the machines this product targets, so this contract carries its own,
// deliberately different rule: any canonical SID that is not on the closed
// list of well-known non-user SIDs. This is a genuinely different rule, not a
// retyped copy.
//
// FAIL-CLOSED DOCTRINE. Any unknown, contradictory, oversized, foreign,
// duplicated, partially written or ambiguous input fails the WHOLE anchor.
// There is no partial acceptance and no repair. No reason, message, path,
// exception text or account name is representable anywhere in the schema or in
// any result.
// ---------------------------------------------------------------------------

/// <summary>
/// Closed set of anchor outcomes. <c>Absent</c> is zero because a default value
/// must never read as a valid anchor.
/// </summary>
public enum ServiceInstallationAnchorOutcome
{
    Absent = 0,
    Valid = 1,
    Malformed = 2,
    Unsupported = 3,
    Invalid = 4,
}

/// <summary>
/// Closed, bounded refusal vocabulary. Every refusal maps to exactly one of
/// these; no exception text, path, account name or free-form message ever
/// escapes the validator.
/// </summary>
public enum ServiceInstallationAnchorInvalidReason
{
    None = 0,
    MalformedJson = 1,
    OversizedInput = 2,
    UnsupportedSchemaVersion = 3,
    UnknownProperty = 4,
    ProhibitedProperty = 5,
    DuplicateProperty = 6,
    MissingProperty = 7,
    WrongType = 8,
    WrongMarker = 9,
    WrongFeatureId = 10,
    InvalidInstallationId = 11,
    InvalidInitiatingUserSid = 12,
    InvalidTimestamp = 13,
}

/// <summary>
/// A validated anchor document. Every member is a bounded string in the exact
/// wire form the validator accepted; nothing here is re-derived or normalised,
/// so a round trip through <see cref="ServiceInstallationAnchorContract.TrySerialize"/>
/// reproduces the accepted bytes exactly.
/// </summary>
public sealed class ServiceInstallationAnchorDocument
{
    public ServiceInstallationAnchorDocument(
        string installationId, string initiatingUserSid, string createdUtc)
    {
        InstallationId = installationId;
        InitiatingUserSid = initiatingUserSid;
        CreatedUtc = createdUtc;
    }

    /// <summary>Canonical lowercase 36-character GUID. IMMUTABLE once written.</summary>
    public string InstallationId { get; }

    /// <summary>Canonical SID string of the NON-ELEVATED initiating user. IMMUTABLE once written.</summary>
    public string InitiatingUserSid { get; }

    /// <summary>Exactly <c>yyyy-MM-ddTHH:mm:ssZ</c>.</summary>
    public string CreatedUtc { get; }

    /// <summary>Carries the bounded type name only - never an id, SID or timestamp.</summary>
    public override string ToString() => nameof(ServiceInstallationAnchorDocument);
}

/// <summary>
/// The whole observable surface of a validation: a bounded outcome, a bounded
/// refusal reason, and - on acceptance only - the document.
/// </summary>
public sealed class ServiceInstallationAnchorValidationResult
{
    private ServiceInstallationAnchorValidationResult(
        ServiceInstallationAnchorOutcome outcome,
        ServiceInstallationAnchorInvalidReason reason,
        ServiceInstallationAnchorDocument? document)
    {
        Outcome = outcome;
        Reason = reason;
        Document = document;
    }

    public ServiceInstallationAnchorOutcome Outcome { get; }

    public ServiceInstallationAnchorInvalidReason Reason { get; }

    public ServiceInstallationAnchorDocument? Document { get; }

    /// <summary>
    /// Acceptance requires BOTH a clean reason and a terminal accepted outcome.
    /// <c>Absent</c> is accepted as a truthful observation and carries no
    /// document; only <c>Valid</c> ever carries one.
    /// </summary>
    public bool IsAccepted =>
        Reason == ServiceInstallationAnchorInvalidReason.None
        && Outcome is ServiceInstallationAnchorOutcome.Absent or ServiceInstallationAnchorOutcome.Valid;

    public bool IsRefused => !IsAccepted;

    internal static ServiceInstallationAnchorValidationResult Absent() =>
        new(ServiceInstallationAnchorOutcome.Absent, ServiceInstallationAnchorInvalidReason.None, null);

    internal static ServiceInstallationAnchorValidationResult Accepted(
        ServiceInstallationAnchorDocument document) =>
        new(ServiceInstallationAnchorOutcome.Valid, ServiceInstallationAnchorInvalidReason.None, document);

    internal static ServiceInstallationAnchorValidationResult Invalid(
        ServiceInstallationAnchorInvalidReason reason) =>
        new(ServiceInstallationAnchorContract.MapRefusalOutcome(reason), reason, null);

    public override string ToString() => Outcome.ToString();
}

/// <summary>
/// The fixed constants, closed property sets and bounded grammars of the
/// installation anchor. Everything here is compile-time or pure.
/// </summary>
public static class ServiceInstallationAnchorContract
{
    /// <summary>The ONLY accepted schema version. There is no v0 and no migration.</summary>
    public const int AnchorSchemaVersion = 1;

    /// <summary>Product marker. A document without this exact value is foreign.</summary>
    public const string ProductAnchorMarker = "PAXCookbook.ServiceInstallationAnchor.v1";

    /// <summary>Feature id. Distinct from the ownership ledger's feature id on purpose.</summary>
    public const string ManagedFeatureId = "service-installation-anchor";

    /// <summary>
    /// Hard input bound, enforced BEFORE parsing. The largest legal anchor is a
    /// few hundred bytes; this leaves generous headroom while making an
    /// oversized or hostile file a cheap, allocation-free refusal.
    /// </summary>
    public const int MaxAnchorBytes = 4096;

    /// <summary>Canonical lowercase GUID "D" format: 8-4-4-4-12.</summary>
    public const int InstallationIdLength = 36;

    /// <summary>Upper bound on a canonical SID string.</summary>
    public const int MaxSidLength = 191;

    /// <summary>Upper bound on the count of SID subauthorities.</summary>
    public const int MaxSidSubAuthorities = 15;

    private const ulong MaxIdentifierAuthority = 0xFFFFFFFFFFFFUL;
    private const ulong MaxSubAuthority = 0xFFFFFFFFUL;

    private static readonly string[] DocumentPropertyNamesStorage =
    {
        "schemaVersion", "productAnchorMarker", "managedFeatureId",
        "installationId", "initiatingUserSid", "createdUtc",
    };

    // Presence alone fails the WHOLE anchor, even when the value is null or
    // empty: the anchor carries a SID and nothing else, so any of these names
    // means the caller is trying to smuggle an account name, a credential or a
    // path across a boundary that carries none of them.
    private static readonly string[] ProhibitedPropertyNamesStorage =
    {
        "upn", "userPrincipalName", "accountName", "account", "userName", "user",
        "displayName", "samAccountName", "domain", "domainName", "computerName",
        "machineName", "email", "mail", "password", "secret", "clientSecret",
        "token", "claim", "claims", "tenantId", "clientId", "privateKey", "pfx",
        "certificate", "path", "filePath", "registryPath", "command", "arguments",
        "environment", "message", "error", "exception", "stackTrace",
    };

    // The closed list of SIDs that can never be an initiating USER. Everything
    // else that parses canonically is accepted, so both the classic
    // S-1-5-21-... and the Entra S-1-12-1-... families pass.
    private static readonly string[] WellKnownNonUserSidsStorage =
    {
        "S-1-0-0",   // Null SID
        "S-1-1-0",   // Everyone
        "S-1-5-7",   // Anonymous Logon
        "S-1-5-11",  // Authenticated Users
        "S-1-5-18",  // Local System
        "S-1-5-19",  // Local Service
        "S-1-5-20",  // Network Service
    };

    public static IReadOnlyList<string> DocumentPropertyNames { get; } =
        Array.AsReadOnly(DocumentPropertyNamesStorage);

    public static IReadOnlyList<string> ProhibitedPropertyNames { get; } =
        Array.AsReadOnly(ProhibitedPropertyNamesStorage);

    internal static readonly HashSet<string> DocumentPropertySet =
        new(DocumentPropertyNamesStorage, StringComparer.Ordinal);

    private static readonly HashSet<string> ProhibitedPropertySet =
        new(ProhibitedPropertyNamesStorage, StringComparer.OrdinalIgnoreCase);

    internal static bool IsProhibitedPropertyName(string name) =>
        ProhibitedPropertySet.Contains(name);

    internal static ServiceInstallationAnchorOutcome MapRefusalOutcome(
        ServiceInstallationAnchorInvalidReason reason) => reason switch
        {
            ServiceInstallationAnchorInvalidReason.None => ServiceInstallationAnchorOutcome.Valid,
            ServiceInstallationAnchorInvalidReason.MalformedJson => ServiceInstallationAnchorOutcome.Malformed,
            ServiceInstallationAnchorInvalidReason.OversizedInput => ServiceInstallationAnchorOutcome.Malformed,
            ServiceInstallationAnchorInvalidReason.UnsupportedSchemaVersion => ServiceInstallationAnchorOutcome.Unsupported,
            _ => ServiceInstallationAnchorOutcome.Invalid,
        };

    /// <summary>
    /// Canonical lowercase GUID "D" form only: 36 characters, lowercase hex,
    /// dashes at 8/13/18/23, and never the all-zero GUID. Braces, uppercase,
    /// "N" form and surrounding whitespace are all refused, so exactly one
    /// spelling of an installation id can ever be persisted.
    /// </summary>
    public static bool IsCanonicalInstallationId(string? value)
    {
        if (value is null || value.Length != InstallationIdLength)
        {
            return false;
        }

        bool allZero = true;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (i is 8 or 13 or 18 or 23)
            {
                if (c != '-')
                {
                    return false;
                }
                continue;
            }
            bool isLowerHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isLowerHex)
            {
                return false;
            }
            if (c != '0')
            {
                allZero = false;
            }
        }

        return !allZero;
    }

    /// <summary>
    /// Canonical SID STRING form only:
    /// <c>S-1-&lt;authority&gt;-&lt;sub&gt;(-&lt;sub&gt;)*</c>, decimal
    /// components, no leading zeros, no whitespace, never a localised account
    /// name. This contract DERIVES no identifier; it only validates supplied
    /// text.
    /// </summary>
    public static bool IsCanonicalSidString(string? sid) => TryParseSid(sid, out _, out _);

    /// <summary>
    /// An INITIATING-USER SID: any canonical SID that is not on the closed
    /// well-known non-user list and is not in a machine/service/builtin family.
    /// See the file header for why this is deliberately broader than the
    /// ownership ledger's S-1-5-21-only user predicate.
    /// </summary>
    public static bool IsInitiatingUserSid(string? sid)
    {
        if (!TryParseSid(sid, out _, out _))
        {
            return false;
        }

        foreach (string wellKnown in WellKnownNonUserSidsStorage)
        {
            if (string.Equals(sid, wellKnown, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // Builtin groups (S-1-5-32-*) and the service family (S-1-5-80*) are
        // never an interactive initiating user.
        return !sid!.StartsWith("S-1-5-32-", StringComparison.Ordinal)
            && !sid.StartsWith("S-1-5-80", StringComparison.Ordinal);
    }

    /// <summary>Exactly <c>yyyy-MM-ddTHH:mm:ssZ</c>. No offset form, no fractional seconds.</summary>
    public static bool IsCanonicalUtcTimestamp(string? value)
    {
        if (value is null || value.Length != 20 || value[19] != 'Z')
        {
            return false;
        }
        return DateTimeOffset.TryParseExact(
            value,
            "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out _);
    }

    /// <summary>
    /// THE IMMUTABILITY COMPARISON. Two anchors describe the same installation
    /// only when BOTH immutable values match exactly, by ordinal comparison.
    /// The timestamp is deliberately NOT part of it: a rewrite of the same
    /// installation by a later run must still be recognised as idempotent, and
    /// a differing timestamp must never be able to look like a conflict.
    /// </summary>
    public static bool HasIdenticalImmutableIdentity(
        ServiceInstallationAnchorDocument? first, ServiceInstallationAnchorDocument? second)
    {
        if (first is null || second is null)
        {
            return false;
        }
        return string.Equals(first.InstallationId, second.InstallationId, StringComparison.Ordinal)
            && string.Equals(first.InitiatingUserSid, second.InitiatingUserSid, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deterministic serialization: fixed property order, two-space indent, LF
    /// line endings, no BOM and a trailing newline. It REFUSES to emit a
    /// document whose fields do not satisfy this contract's own grammars, so an
    /// invalid anchor can never reach disk and no JSON string escaping is ever
    /// required (every accepted grammar is a strict subset of unescaped ASCII).
    /// </summary>
    public static bool TrySerialize(ServiceInstallationAnchorDocument? document, out string json)
    {
        json = string.Empty;
        if (document is null
            || !IsCanonicalInstallationId(document.InstallationId)
            || !IsInitiatingUserSid(document.InitiatingUserSid)
            || !IsCanonicalUtcTimestamp(document.CreatedUtc))
        {
            return false;
        }

        var builder = new StringBuilder(320);
        builder.Append("{\n");
        builder.Append("  \"schemaVersion\": ")
            .Append(AnchorSchemaVersion.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        builder.Append("  \"productAnchorMarker\": \"").Append(ProductAnchorMarker).Append("\",\n");
        builder.Append("  \"managedFeatureId\": \"").Append(ManagedFeatureId).Append("\",\n");
        builder.Append("  \"installationId\": \"").Append(document.InstallationId).Append("\",\n");
        builder.Append("  \"initiatingUserSid\": \"").Append(document.InitiatingUserSid).Append("\",\n");
        builder.Append("  \"createdUtc\": \"").Append(document.CreatedUtc).Append("\"\n");
        builder.Append("}\n");

        json = builder.ToString();
        return true;
    }

    private static bool TryParseSid(string? sid, out ulong authority, out uint[] subAuthorities)
    {
        authority = 0UL;
        subAuthorities = Array.Empty<uint>();

        if (sid is null || sid.Length == 0 || sid.Length > MaxSidLength)
        {
            return false;
        }

        string[] parts = sid.Split('-');
        if (parts.Length < 4)
        {
            return false;
        }
        if (!string.Equals(parts[0], "S", StringComparison.Ordinal)
            || !string.Equals(parts[1], "1", StringComparison.Ordinal))
        {
            return false;
        }
        if (!TryParseCanonicalDecimal(parts[2], out authority) || authority > MaxIdentifierAuthority)
        {
            return false;
        }

        int count = parts.Length - 3;
        if (count < 1 || count > MaxSidSubAuthorities)
        {
            return false;
        }

        var subs = new uint[count];
        for (int i = 0; i < count; i++)
        {
            if (!TryParseCanonicalDecimal(parts[3 + i], out ulong value) || value > MaxSubAuthority)
            {
                return false;
            }
            subs[i] = (uint)value;
        }

        subAuthorities = subs;
        return true;
    }

    private static bool TryParseCanonicalDecimal(string raw, out ulong value)
    {
        value = 0UL;
        if (raw.Length == 0 || raw.Length > 20)
        {
            return false;
        }
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] < '0' || raw[i] > '9')
            {
                return false;
            }
        }
        if (raw.Length > 1 && raw[0] == '0')
        {
            return false;
        }
        return ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}

/// <summary>
/// The pure, deterministic anchor validator. It reads nothing, writes nothing
/// and throws nothing: every failure - including a hostile, truncated or
/// partially written input - maps to a bounded refusal reason.
/// </summary>
public static class ServiceInstallationAnchorValidator
{
    /// <summary>
    /// The ONLY way to obtain <c>Absent</c>. A caller that has genuinely
    /// observed "no anchor file" says so explicitly; null, empty or blank text
    /// is never treated as absence, because that would let a truncated write
    /// read as a clean machine and then be silently overwritten.
    /// </summary>
    public static ServiceInstallationAnchorValidationResult ForAbsentAnchor() =>
        ServiceInstallationAnchorValidationResult.Absent();

    /// <summary>
    /// The ONLY way for an I/O owner to report a refusal it observed BEFORE any
    /// content could be validated - an oversized file it declined to read, or
    /// bytes that are not decodable text at all. It manufactures a REFUSAL and
    /// nothing else: <see cref="ServiceInstallationAnchorInvalidReason.None"/> is
    /// remapped to <see cref="ServiceInstallationAnchorInvalidReason.MalformedJson"/>
    /// so this can never be used to fabricate an acceptance.
    /// </summary>
    public static ServiceInstallationAnchorValidationResult ForRefusedAnchor(
        ServiceInstallationAnchorInvalidReason reason)
    {
        ServiceInstallationAnchorInvalidReason bounded =
            reason == ServiceInstallationAnchorInvalidReason.None
                ? ServiceInstallationAnchorInvalidReason.MalformedJson
                : reason;
        return ServiceInstallationAnchorValidationResult.Invalid(bounded);
    }

    public static ServiceInstallationAnchorValidationResult Validate(string? anchorJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(anchorJson))
            {
                return Invalid(ServiceInstallationAnchorInvalidReason.MalformedJson);
            }

            int byteCount;
            try
            {
                byteCount = Encoding.UTF8.GetByteCount(anchorJson);
            }
            catch (Exception)
            {
                return Invalid(ServiceInstallationAnchorInvalidReason.MalformedJson);
            }
            if (byteCount > ServiceInstallationAnchorContract.MaxAnchorBytes)
            {
                return Invalid(ServiceInstallationAnchorInvalidReason.OversizedInput);
            }

            JsonDocument parsed;
            try
            {
                parsed = JsonDocument.Parse(anchorJson);
            }
            catch (Exception)
            {
                return Invalid(ServiceInstallationAnchorInvalidReason.MalformedJson);
            }

            using (parsed)
            {
                return ValidateRoot(parsed.RootElement);
            }
        }
        catch (Exception)
        {
            return Invalid(ServiceInstallationAnchorInvalidReason.MalformedJson);
        }
    }

    private static ServiceInstallationAnchorValidationResult Invalid(
        ServiceInstallationAnchorInvalidReason reason) =>
        ServiceInstallationAnchorValidationResult.Invalid(reason);

    private static ServiceInstallationAnchorValidationResult ValidateRoot(JsonElement root)
    {
        ServiceInstallationAnchorInvalidReason shape = ValidateClosedObject(
            root,
            ServiceInstallationAnchorContract.DocumentPropertySet,
            ServiceInstallationAnchorContract.DocumentPropertyNames);
        if (shape != ServiceInstallationAnchorInvalidReason.None)
        {
            return Invalid(shape);
        }

        if (!TryStrictInt(root, "schemaVersion", out int schemaVersion))
        {
            return Invalid(ServiceInstallationAnchorInvalidReason.WrongType);
        }
        if (schemaVersion != ServiceInstallationAnchorContract.AnchorSchemaVersion)
        {
            return Invalid(ServiceInstallationAnchorInvalidReason.UnsupportedSchemaVersion);
        }

        if (!TryString(root, "productAnchorMarker", out string marker))
        {
            return Invalid(ServiceInstallationAnchorInvalidReason.WrongType);
        }
        if (!string.Equals(marker, ServiceInstallationAnchorContract.ProductAnchorMarker, StringComparison.Ordinal))
        {
            return Invalid(ServiceInstallationAnchorInvalidReason.WrongMarker);
        }

        if (!TryString(root, "managedFeatureId", out string featureId))
        {
            return Invalid(ServiceInstallationAnchorInvalidReason.WrongType);
        }
        if (!string.Equals(featureId, ServiceInstallationAnchorContract.ManagedFeatureId, StringComparison.Ordinal))
        {
            return Invalid(ServiceInstallationAnchorInvalidReason.WrongFeatureId);
        }

        if (!TryString(root, "installationId", out string installationId))
        {
            return Invalid(ServiceInstallationAnchorInvalidReason.WrongType);
        }
        if (!ServiceInstallationAnchorContract.IsCanonicalInstallationId(installationId))
        {
            return Invalid(ServiceInstallationAnchorInvalidReason.InvalidInstallationId);
        }

        if (!TryString(root, "initiatingUserSid", out string initiatingUserSid))
        {
            return Invalid(ServiceInstallationAnchorInvalidReason.WrongType);
        }
        if (!ServiceInstallationAnchorContract.IsInitiatingUserSid(initiatingUserSid))
        {
            return Invalid(ServiceInstallationAnchorInvalidReason.InvalidInitiatingUserSid);
        }

        if (!TryString(root, "createdUtc", out string createdUtc))
        {
            return Invalid(ServiceInstallationAnchorInvalidReason.WrongType);
        }
        if (!ServiceInstallationAnchorContract.IsCanonicalUtcTimestamp(createdUtc))
        {
            return Invalid(ServiceInstallationAnchorInvalidReason.InvalidTimestamp);
        }

        return ServiceInstallationAnchorValidationResult.Accepted(
            new ServiceInstallationAnchorDocument(installationId, initiatingUserSid, createdUtc));
    }

    private static ServiceInstallationAnchorInvalidReason ValidateClosedObject(
        JsonElement element,
        HashSet<string> allowed,
        IReadOnlyList<string> required)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return ServiceInstallationAnchorInvalidReason.MalformedJson;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return ServiceInstallationAnchorInvalidReason.DuplicateProperty;
            }
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (ServiceInstallationAnchorContract.IsProhibitedPropertyName(property.Name))
            {
                return ServiceInstallationAnchorInvalidReason.ProhibitedProperty;
            }
            if (!allowed.Contains(property.Name))
            {
                return ServiceInstallationAnchorInvalidReason.UnknownProperty;
            }
        }

        for (int i = 0; i < required.Count; i++)
        {
            if (!seen.Contains(required[i]))
            {
                return ServiceInstallationAnchorInvalidReason.MissingProperty;
            }
        }

        return ServiceInstallationAnchorInvalidReason.None;
    }

    private static bool TryString(JsonElement parent, string name, out string value)
    {
        value = string.Empty;
        if (!parent.TryGetProperty(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = element.GetString() ?? string.Empty;
        return true;
    }

    /// <summary>
    /// A JSON integer with no fractional part, no exponent, no leading zero and
    /// no sign-only form. <c>1.0</c>, <c>"1"</c> and <c>1e0</c> are all refused.
    /// </summary>
    private static bool TryStrictInt(JsonElement parent, string name, out int value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        string raw = element.GetRawText();
        if (raw.Length == 0 || raw.Length > 11)
        {
            return false;
        }

        int start = raw[0] == '-' ? 1 : 0;
        if (start >= raw.Length)
        {
            return false;
        }
        for (int i = start; i < raw.Length; i++)
        {
            if (raw[i] < '0' || raw[i] > '9')
            {
                return false;
            }
        }
        if (raw.Length - start > 1 && raw[start] == '0')
        {
            return false;
        }

        return int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}
