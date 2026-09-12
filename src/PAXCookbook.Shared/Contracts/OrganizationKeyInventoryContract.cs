using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PAXCookbook.Shared.Contracts;

// Canonical, cross-component contract for the ORGANIZATION-KEY INVENTORY SOURCE
// BOUNDARY (Cycle 05).
//
// This contract defines the SHAPE and the SOURCE BOUNDARY for a FUTURE
// organization-provided Chef's Keys inventory WITHOUT activating, connecting to,
// or accessing ANY real key. It is a parsing/validation/status foundation ONLY.
// It does NOT: read a filesystem inventory, read ProgramData, read the registry,
// discover/select/validate a certificate, touch a certificate store, read a
// private key, obtain a service-backed credential, reach a Windows Credential
// Manager target, query a tenant, query Microsoft Graph, acquire a token, use
// WAM/Windows Hello, install/contact a service, run an elevated helper, add a
// Cook pipeline, run PAX, perform a Bake, or activate ANY capability. Being able
// to PARSE a provisioned document never implies a key is available, resolved,
// usable, bound to a Recipe, or authorized for a Bake.
//
// SINGLE SOURCE OF TRUTH. Compiled into PAXCookbook.Shared (used by Setup) AND
// linked directly into the native host (PAXCookbook.App, which does not reference
// Shared) via the established <Compile Include=... Link=...> pattern, so both use
// the exact same limits, tokens, and validation with no duplicated constants
// that can drift. It depends only on System, System.Collections.Generic,
// System.Text, and System.Text.Json — NO Win32, NO certificate APIs, NO network,
// NO filesystem — so it links cleanly into the App and compiles under Shared's
// plain net8.0 target.
//
// LAYERED SEPARATION (being at layer N never implies layer N+1):
//   1 policy authorization  — Cycle 4 gate: machine policy permits an inventory.
//   2 inventory shape/source boundary — THIS cycle (contract + parser + source).
//   3 inventory availability — a PROVISIONED document (test-only synthetic).
//   4 certificate resolution/usability — NEVER true this cycle.
//   5 Recipe binding — NEVER true this cycle.
//   6 Bake authorization / service readiness — NEVER true this cycle.
// The shipped terminal state is `authorized_not_provisioned`, layer 1 only,
// because the production source is DISABLED and performs ZERO I/O.
//
// FAIL-CLOSED DOCTRINE (binding): a null gate, an unauthorized gate, a null
// source, a source that reports unavailable/untrusted, a malformed/oversized
// document, and every structural/allow-list/prohibited violation resolve to a
// bounded non-provisioned/unavailable/invalid/untrusted state — NEVER to a
// provisioned or usable state, and NEVER to a permissive fallback. The whole
// inventory fails closed on the first violation; a single bad entry rejects the
// entire document. No identifier, raw value, secret, exception text, path, or
// registry key is ever represented on the projection or in ToString.

// Bounded limits, tokens, and allow/deny sets for the organization inventory.
// Every constant is a compile-time boundary; there is no runtime override, no
// environment variable, no command-line switch, and no user/HTTP-supplied
// configuration that can widen any of these.
public static class OrganizationKeyInventoryContract
{
    // Only schema/entry version 1 is representable; any other value is invalid.
    public const int SchemaVersion = 1;
    public const int EntryVersion = 1;

    // Cycle 10 adds schema/entry version 2, which carries a certificate REFERENCE.
    // Exactly {1, 2} are representable; any other value is invalid, and an entry's
    // entryVersion must equal the document's schemaVersion.
    public const int SchemaVersionV1 = 1;
    public const int SchemaVersionV2 = 2;

    // A certificate reference is the lowercase/uppercase-insensitive hex SHA-256
    // of the certificate's DER bytes: 64 hex characters, nothing else. It is a
    // PUBLIC digest — not certificate bytes, not a private-key identifier, not a
    // path, not a store selector, and never authority to delete or mutate.
    public const int CertificateReferenceLength = 64;

    // A document larger than this (measured in UTF-8 bytes BEFORE any parse) is
    // rejected outright, so an oversized blob is never handed to the JSON reader.
    public const int MaxDocumentBytes = 65536;

    // A document may describe at most this many organization keys.
    public const int MaxEntries = 64;

    // The maximum length (UTF-16 code units) of any bounded string field.
    public const int MaxStringLength = 128;

    // The ONLY permitted authentication reference type. App-registration secret,
    // web login, device code, and managed identity are NOT representable here:
    // organization origin is certificate-only, and secrets are prohibited.
    public const string CertificateReferenceType = "app_registration_certificate";

    // The two permitted administrator states for an entry.
    public const string AdminStateEnabled = "enabled";
    public const string AdminStateDisabled = "disabled";

    // Exactly the two permitted top-level property names.
    internal static readonly HashSet<string> AllowedTopLevelKeys =
        new(StringComparer.Ordinal) { "schemaVersion", "entries" };

    // Exactly the permitted entry property names. certificateReferenceType is a
    // distinct key from the prohibited "certificate"; membership is by exact
    // ordinal name, never by substring.
    internal static readonly HashSet<string> AllowedEntryKeys =
        new(StringComparer.Ordinal)
        {
            "entryVersion", "organizationKeyId", "displayName",
            "certificateReferenceType", "adminState", "tenantReference", "clientReference",
            "certificateSha256",
        };

    // Keys that reject the WHOLE inventory even when present with a null/empty
    // value. These name secret material, credential/vault targets, filesystem or
    // registry paths, service/command execution, cloud/Graph/auth surfaces, and
    // Recipe/Bake wiring — none of which an inventory document may ever carry.
    // Comparison is ordinal-ignore-case so a case variant cannot slip past.
    internal static readonly HashSet<string> ProhibitedKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "clientSecret", "secret", "hasSecret", "secretPresent", "password",
            "certificate", "certificateBytes", "pfx", "privateKey", "privateKeyBytes",
            "token", "claim", "claims", "upn", "account", "interactiveAccount",
            "wamAccount", "credentialManagerTarget", "wcmTarget", "path", "filePath",
            "inventoryPath", "registryPath", "storeName", "certStore", "serviceName",
            "command", "script", "arguments", "args", "env", "environment", "scope",
            "scopes", "graphEndpoint", "query", "recipe", "bake", "authType",
            "authenticationType",
        };

    internal static bool IsProhibited(string name) => ProhibitedKeys.Contains(name);

    internal static bool IsAllowedEntryKey(string name) => AllowedEntryKeys.Contains(name);

    // A bounded, opaque organization-key identifier: letters, digits, '.', '_',
    // '-' only. Forbidding ':' structurally prevents collision with the personal
    // Windows-Credential-Manager namespace (which uses colon-delimited targets),
    // so an organization id can never masquerade as a personal Chef's Key target.
    internal static bool IsValidIdCharset(string id)
    {
        foreach (char c in id)
        {
            bool ok = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '.' || c == '_' || c == '-';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    // Exactly 64 hex characters. Whitespace, ':'/'-'/space separators, and an
    // "0x" prefix are all rejected rather than tolerated, so only one textual
    // form of a reference is representable.
    internal static bool IsValidCertificateReference(string? value)
    {
        if (value is null || value.Length != CertificateReferenceLength)
        {
            return false;
        }
        foreach (char c in value)
        {
            bool ok = (c >= '0' && c <= '9')
                || (c >= 'a' && c <= 'f')
                || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    // The single canonical in-memory form. Duplicate detection and resolution
    // matching both operate on this normalized value, so a case variant can
    // never be treated as a distinct reference.
    internal static string NormalizeCertificateReference(string value)
        => value.ToUpperInvariant();
}

// The outcome of parsing an inventory document.
public enum OrganizationKeyInventoryParseOutcome
{
    Valid,
    Invalid,
}

// The bounded reason an inventory document was rejected. Each value is a
// content-free classification token; none carries a raw value, identifier,
// path, secret, or exception text.
public enum OrganizationKeyInventoryInvalidReason
{
    None,
    MalformedJson,
    OversizedDocument,
    UnsupportedSchema,
    UnknownField,
    ProhibitedField,
    MissingField,
    WrongType,
    EmptyValue,
    OversizedField,
    TooManyEntries,
    InvalidId,
    DuplicateId,
    DuplicateProperty,
    InvalidEntries,

    // The trusted bytes could not be decoded as strict UTF-8 (e.g. a non-UTF-8
    // byte sequence or a non-UTF-8 byte-order mark). Content-free by design.
    InvalidEncoding,

    // A schema-v2 certificate reference was not exactly 64 hex characters.
    InvalidCertificateReference,

    // Two entries named the same certificate reference (compared normalized), so
    // the whole document is ambiguous and fails closed.
    DuplicateCertificateReference,
}

// The bounded terminal state of the inventory evaluation. Impossible transitions
// are unrepresentable: the projection has a private ctor and one factory per
// state, and availability is asserted by exactly one state.
public enum OrganizationInventoryState
{
    NotAuthorized,
    AuthorizedNotProvisioned,
    AuthorizedProvisioned,
    Unavailable,
    Invalid,
    Untrusted,
}

// What a read-only source reported. `Provided` carries an opaque document string
// for the parser; every other kind carries nothing.
public enum OrganizationInventorySourceKind
{
    NotProvisioned,
    Unavailable,
    Untrusted,
    Provided,

    // The source observed a TRUSTED object but its bytes could not be decoded as
    // strict UTF-8. This is distinct from Untrusted (a trust failure) and from
    // Provided (decodable bytes handed to the parser): the object was trusted,
    // but its content is undecodable, so it fails closed to an invalid state.
    InvalidContent,
}

// An immutable, fixed-origin organization inventory entry. It is produced ONLY
// by the parser and carries a fixed `Origin` of Organization so it can never be
// confused with a personal Chef's Key. The tenant/client references are opaque
// bounded tokens carried IN-PROCESS only; they are internal and are NEVER
// projected onto the wire, logged, or included in ToString.
public sealed class OrganizationKeyInventoryEntry
{
    private OrganizationKeyInventoryEntry(
        int entryVersion,
        string organizationKeyId,
        string displayName,
        string certificateReferenceType,
        string adminState,
        string tenantReference,
        string clientReference,
        string certificateSha256)
    {
        EntryVersion = entryVersion;
        OrganizationKeyId = organizationKeyId;
        DisplayName = displayName;
        CertificateReferenceType = certificateReferenceType;
        AdminState = adminState;
        TenantReference = tenantReference;
        ClientReference = clientReference;
        CertificateSha256 = certificateSha256;
    }

    // Fixed by construction: an inventory entry is always Organization origin.
    public ChefKeyOrigin Origin => ChefKeyOrigin.Organization;

    public int EntryVersion { get; }

    public string OrganizationKeyId { get; }

    public string DisplayName { get; }

    public string CertificateReferenceType { get; }

    public string AdminState { get; }

    // The normalized (uppercase) public SHA-256 of the certificate's DER bytes
    // for a schema-v2 entry, and string.Empty for a schema-v1 entry. It is a
    // reference only: it resolves nothing, unlocks nothing, and carries no
    // certificate bytes, private-key handle, path, or store selector.
    public string CertificateSha256 { get; }

    // Opaque, bounded, in-process references. Internal so they cannot leak onto
    // the wire; a certificate is never resolved and no tenant/client is queried.
    internal string TenantReference { get; }

    internal string ClientReference { get; }

    // Parser-only factory. There is no public constructor, so an entry cannot be
    // fabricated outside validated parsing.
    internal static OrganizationKeyInventoryEntry Create(
        int entryVersion,
        string organizationKeyId,
        string displayName,
        string certificateReferenceType,
        string adminState,
        string tenantReference,
        string clientReference,
        string certificateSha256)
        => new(entryVersion, organizationKeyId, displayName, certificateReferenceType,
               adminState, tenantReference, clientReference, certificateSha256);
}

// The immutable, bounded result of parsing an inventory document. On any failure
// the entry list is empty and the reason is bounded. No raw JSON, value, or
// exception text is ever represented.
public sealed class OrganizationKeyInventoryParseResult
{
    private static readonly IReadOnlyList<OrganizationKeyInventoryEntry> EmptyEntries =
        Array.Empty<OrganizationKeyInventoryEntry>();

    private OrganizationKeyInventoryParseResult(
        OrganizationKeyInventoryParseOutcome outcome,
        OrganizationKeyInventoryInvalidReason reason,
        IReadOnlyList<OrganizationKeyInventoryEntry> entries)
    {
        Outcome = outcome;
        Reason = reason;
        Entries = entries;
    }

    public OrganizationKeyInventoryParseOutcome Outcome { get; }

    public bool IsValid => Outcome == OrganizationKeyInventoryParseOutcome.Valid;

    public OrganizationKeyInventoryInvalidReason Reason { get; }

    public IReadOnlyList<OrganizationKeyInventoryEntry> Entries { get; }

    public int EntryCount => Entries.Count;

    internal static OrganizationKeyInventoryParseResult Valid(IReadOnlyList<OrganizationKeyInventoryEntry> entries)
        => new(OrganizationKeyInventoryParseOutcome.Valid, OrganizationKeyInventoryInvalidReason.None, entries);

    internal static OrganizationKeyInventoryParseResult Invalid(OrganizationKeyInventoryInvalidReason reason)
        => new(OrganizationKeyInventoryParseOutcome.Invalid, reason, EmptyEntries);
}

// The pure, deterministic, side-effect-free inventory parser/validator. It reads
// only the string it is given, never touches the filesystem/registry/network/
// certificate store/credential vault, never throws through the boundary, and
// fails closed on the first violation for the WHOLE document.
public static class OrganizationKeyInventoryParser
{
    public static OrganizationKeyInventoryParseResult Parse(string? document)
    {
        if (document is null)
        {
            return OrganizationKeyInventoryParseResult.Invalid(
                OrganizationKeyInventoryInvalidReason.MalformedJson);
        }

        // Size guard runs BEFORE the JSON reader so an oversized blob is never
        // parsed for content.
        if (Encoding.UTF8.GetByteCount(document) > OrganizationKeyInventoryContract.MaxDocumentBytes)
        {
            return OrganizationKeyInventoryParseResult.Invalid(
                OrganizationKeyInventoryInvalidReason.OversizedDocument);
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(document);
        }
        catch (JsonException)
        {
            return OrganizationKeyInventoryParseResult.Invalid(
                OrganizationKeyInventoryInvalidReason.MalformedJson);
        }

        using (parsed)
        {
            try
            {
                return ValidateRoot(parsed.RootElement);
            }
            catch (Exception)
            {
                // Fail closed on any unexpected reader condition; never surface
                // an exception across the boundary.
                return OrganizationKeyInventoryParseResult.Invalid(
                    OrganizationKeyInventoryInvalidReason.MalformedJson);
            }
        }
    }

    private static OrganizationKeyInventoryParseResult ValidateRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return OrganizationKeyInventoryParseResult.Invalid(
                OrganizationKeyInventoryInvalidReason.MalformedJson);
        }

        // Duplicate top-level property names reject the document.
        var topSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in root.EnumerateObject())
        {
            if (!topSeen.Add(p.Name))
            {
                return OrganizationKeyInventoryParseResult.Invalid(
                    OrganizationKeyInventoryInvalidReason.DuplicateProperty);
            }
        }

        // Strict allow-list: any property that is not exactly schemaVersion or
        // entries rejects the document (prohibited names are reported distinctly).
        foreach (JsonProperty p in root.EnumerateObject())
        {
            if (OrganizationKeyInventoryContract.AllowedTopLevelKeys.Contains(p.Name))
            {
                continue;
            }
            return OrganizationKeyInventoryParseResult.Invalid(
                OrganizationKeyInventoryContract.IsProhibited(p.Name)
                    ? OrganizationKeyInventoryInvalidReason.ProhibitedField
                    : OrganizationKeyInventoryInvalidReason.UnknownField);
        }

        if (!root.TryGetProperty("schemaVersion", out JsonElement schema)
            || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out int schemaValue)
            || (schemaValue != OrganizationKeyInventoryContract.SchemaVersionV1
                && schemaValue != OrganizationKeyInventoryContract.SchemaVersionV2))
        {
            return OrganizationKeyInventoryParseResult.Invalid(
                OrganizationKeyInventoryInvalidReason.UnsupportedSchema);
        }

        if (!root.TryGetProperty("entries", out JsonElement entriesElement)
            || entriesElement.ValueKind != JsonValueKind.Array)
        {
            return OrganizationKeyInventoryParseResult.Invalid(
                OrganizationKeyInventoryInvalidReason.InvalidEntries);
        }

        if (entriesElement.GetArrayLength() > OrganizationKeyInventoryContract.MaxEntries)
        {
            return OrganizationKeyInventoryParseResult.Invalid(
                OrganizationKeyInventoryInvalidReason.TooManyEntries);
        }

        var entries = new List<OrganizationKeyInventoryEntry>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement entryElement in entriesElement.EnumerateArray())
        {
            (OrganizationKeyInventoryInvalidReason reason, OrganizationKeyInventoryEntry? entry) =
                ValidateEntry(entryElement, schemaValue);
            if (reason != OrganizationKeyInventoryInvalidReason.None)
            {
                return OrganizationKeyInventoryParseResult.Invalid(reason);
            }

            // Duplicate organization ids (ordinal-ignore-case) reject the document.
            if (!ids.Add(entry!.OrganizationKeyId))
            {
                return OrganizationKeyInventoryParseResult.Invalid(
                    OrganizationKeyInventoryInvalidReason.DuplicateId);
            }

            // Duplicate certificate references are compared on the NORMALIZED
            // value, so a case variant cannot smuggle in a second claim to the
            // same certificate.
            if (entry.CertificateSha256.Length != 0 && !references.Add(entry.CertificateSha256))
            {
                return OrganizationKeyInventoryParseResult.Invalid(
                    OrganizationKeyInventoryInvalidReason.DuplicateCertificateReference);
            }
            entries.Add(entry);
        }

        return OrganizationKeyInventoryParseResult.Valid(entries);
    }

    private static (OrganizationKeyInventoryInvalidReason Reason, OrganizationKeyInventoryEntry? Entry)
        ValidateEntry(JsonElement entry, int schemaVersion)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return (OrganizationKeyInventoryInvalidReason.InvalidEntries, null);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty p in entry.EnumerateObject())
        {
            if (!seen.Add(p.Name))
            {
                return (OrganizationKeyInventoryInvalidReason.DuplicateProperty, null);
            }
        }

        foreach (JsonProperty p in entry.EnumerateObject())
        {
            if (OrganizationKeyInventoryContract.IsAllowedEntryKey(p.Name))
            {
                continue;
            }
            return (OrganizationKeyInventoryContract.IsProhibited(p.Name)
                ? OrganizationKeyInventoryInvalidReason.ProhibitedField
                : OrganizationKeyInventoryInvalidReason.UnknownField, null);
        }

        if (!entry.TryGetProperty("entryVersion", out JsonElement ev) || ev.ValueKind == JsonValueKind.Null)
        {
            return (OrganizationKeyInventoryInvalidReason.MissingField, null);
        }
        if (ev.ValueKind != JsonValueKind.Number)
        {
            return (OrganizationKeyInventoryInvalidReason.WrongType, null);
        }
        if (!ev.TryGetInt32(out int entryVersion) || entryVersion != schemaVersion)
        {
            return (OrganizationKeyInventoryInvalidReason.UnsupportedSchema, null);
        }

        (OrganizationKeyInventoryInvalidReason idReason, string? organizationKeyId) =
            ReadBoundedString(entry, "organizationKeyId");
        if (idReason != OrganizationKeyInventoryInvalidReason.None)
        {
            return (idReason, null);
        }
        if (!OrganizationKeyInventoryContract.IsValidIdCharset(organizationKeyId!))
        {
            return (OrganizationKeyInventoryInvalidReason.InvalidId, null);
        }

        (OrganizationKeyInventoryInvalidReason nameReason, string? displayName) =
            ReadBoundedString(entry, "displayName");
        if (nameReason != OrganizationKeyInventoryInvalidReason.None)
        {
            return (nameReason, null);
        }

        (OrganizationKeyInventoryInvalidReason certReason, string? certificateReferenceType) =
            ReadBoundedString(entry, "certificateReferenceType");
        if (certReason != OrganizationKeyInventoryInvalidReason.None)
        {
            return (certReason, null);
        }
        if (!string.Equals(certificateReferenceType, OrganizationKeyInventoryContract.CertificateReferenceType, StringComparison.Ordinal))
        {
            return (OrganizationKeyInventoryInvalidReason.WrongType, null);
        }

        (OrganizationKeyInventoryInvalidReason adminReason, string? adminState) =
            ReadBoundedString(entry, "adminState");
        if (adminReason != OrganizationKeyInventoryInvalidReason.None)
        {
            return (adminReason, null);
        }
        if (!string.Equals(adminState, OrganizationKeyInventoryContract.AdminStateEnabled, StringComparison.Ordinal)
            && !string.Equals(adminState, OrganizationKeyInventoryContract.AdminStateDisabled, StringComparison.Ordinal))
        {
            return (OrganizationKeyInventoryInvalidReason.WrongType, null);
        }

        (OrganizationKeyInventoryInvalidReason tenantReason, string? tenantReference) =
            ReadBoundedString(entry, "tenantReference");
        if (tenantReason != OrganizationKeyInventoryInvalidReason.None)
        {
            return (tenantReason, null);
        }

        (OrganizationKeyInventoryInvalidReason clientReason, string? clientReference) =
            ReadBoundedString(entry, "clientReference");
        if (clientReason != OrganizationKeyInventoryInvalidReason.None)
        {
            return (clientReason, null);
        }

        (OrganizationKeyInventoryInvalidReason refReason, string? certificateSha256) =
            ReadCertificateReference(entry, schemaVersion);
        if (refReason != OrganizationKeyInventoryInvalidReason.None)
        {
            return (refReason, null);
        }

        return (OrganizationKeyInventoryInvalidReason.None,
            OrganizationKeyInventoryEntry.Create(
                entryVersion, organizationKeyId!, displayName!, certificateReferenceType!,
                adminState!, tenantReference!, clientReference!, certificateSha256!));
    }

    // A schema-v1 entry carries NO reference: naming one is an unknown field for
    // that version. A schema-v2 entry MUST carry a well-formed one.
    private static (OrganizationKeyInventoryInvalidReason Reason, string? Value)
        ReadCertificateReference(JsonElement entry, int schemaVersion)
    {
        bool present = entry.TryGetProperty("certificateSha256", out JsonElement value);

        if (schemaVersion == OrganizationKeyInventoryContract.SchemaVersionV1)
        {
            return present
                ? (OrganizationKeyInventoryInvalidReason.UnknownField, null)
                : (OrganizationKeyInventoryInvalidReason.None, string.Empty);
        }

        if (!present || value.ValueKind == JsonValueKind.Null)
        {
            return (OrganizationKeyInventoryInvalidReason.MissingField, null);
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            return (OrganizationKeyInventoryInvalidReason.WrongType, null);
        }

        string raw = value.GetString() ?? string.Empty;
        if (!OrganizationKeyInventoryContract.IsValidCertificateReference(raw))
        {
            return (OrganizationKeyInventoryInvalidReason.InvalidCertificateReference, null);
        }
        return (OrganizationKeyInventoryInvalidReason.None,
            OrganizationKeyInventoryContract.NormalizeCertificateReference(raw));
    }

    private static (OrganizationKeyInventoryInvalidReason Reason, string? Value)
        ReadBoundedString(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return (OrganizationKeyInventoryInvalidReason.MissingField, null);
        }
        if (value.ValueKind != JsonValueKind.String)
        {
            return (OrganizationKeyInventoryInvalidReason.WrongType, null);
        }
        string s = value.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(s))
        {
            return (OrganizationKeyInventoryInvalidReason.EmptyValue, null);
        }
        if (s.Length > OrganizationKeyInventoryContract.MaxStringLength)
        {
            return (OrganizationKeyInventoryInvalidReason.OversizedField, null);
        }
        return (OrganizationKeyInventoryInvalidReason.None, s);
    }
}

// The bounded result a read-only source reports. `Provided` carries the opaque
// document for the parser; every other kind carries nothing. The document is
// internal so only the evaluator can read it.
public sealed class OrganizationInventorySourceResult
{
    private OrganizationInventorySourceResult(OrganizationInventorySourceKind kind, string? document)
    {
        Kind = kind;
        Document = document;
    }

    public OrganizationInventorySourceKind Kind { get; }

    internal string? Document { get; }

    public static OrganizationInventorySourceResult NotProvisioned()
        => new(OrganizationInventorySourceKind.NotProvisioned, null);

    public static OrganizationInventorySourceResult Unavailable()
        => new(OrganizationInventorySourceKind.Unavailable, null);

    public static OrganizationInventorySourceResult Untrusted()
        => new(OrganizationInventorySourceKind.Untrusted, null);

    public static OrganizationInventorySourceResult Provided(string document)
        => new(OrganizationInventorySourceKind.Provided, document ?? string.Empty);

    // A trusted object whose bytes could not be decoded as strict UTF-8. It
    // carries NO document; the evaluator maps it to a bounded invalid state.
    public static OrganizationInventorySourceResult InvalidContent()
        => new(OrganizationInventorySourceKind.InvalidContent, null);
}

// The read-only source abstraction. A source only REPORTS what it observed; it
// never mutates anything and never returns a live key. The single shipped
// implementation is disabled.
public interface IOrganizationKeyInventorySource
{
    OrganizationInventorySourceResult Load();
}

// The ONLY production source this cycle. It is permanently disabled: it performs
// ZERO I/O (no filesystem, no ProgramData, no registry, no certificate store, no
// credential vault, no network) and always reports NotProvisioned. There is no
// configuration, environment variable, command-line switch, or user/HTTP input
// that can make it read a real inventory.
public sealed class NotProvisionedOrganizationKeyInventorySource : IOrganizationKeyInventorySource
{
    public OrganizationInventorySourceResult Load()
        => OrganizationInventorySourceResult.NotProvisioned();
}

// The immutable, bounded status projection that may cross the broker/API/UI
// boundary. Impossible states are hard to represent: a PRIVATE ctor plus one
// factory per state. EVERY later-stage capability is constant-false by
// construction, and availability (`InventoryLoaded`) is true ONLY for the
// provisioned state. The projection carries NO identifier, NO tenant/client
// reference, NO certificate data, NO secret, NO path, and NO raw source — only
// bounded state/reason tokens and a non-negative entry count.
public sealed class OrganizationInventoryProjection
{
    private OrganizationInventoryProjection(
        OrganizationInventoryState state,
        string wireState,
        string wireReason,
        int entryCount)
    {
        State = state;
        WireState = wireState;
        WireReason = wireReason;
        EntryCount = entryCount;
    }

    public OrganizationInventoryState State { get; }

    public string WireState { get; }

    public string WireReason { get; }

    // Count of provisioned entries; 0 in every non-provisioned state.
    public int EntryCount { get; }

    // Every later stage is unconditionally false this cycle: no certificate is
    // resolved, no private key is available, nothing is usable, nothing is bound
    // to a Recipe, no Bake is authorized, and no service is ready.
    public bool CertificateResolved => false;

    public bool PrivateKeyAvailable => false;

    public bool Usable => false;

    public bool RecipeBound => false;

    public bool BakeAuthorized => false;

    public bool ServiceReady => false;

    // Bounded FUTURE-constraint markers, constant by construction.
    public bool ReadOnly => true;

    public bool CertificateOnly => true;

    // Availability is asserted ONLY by the provisioned state. Production never
    // reaches it (the shipped source is disabled), so production stays false.
    public bool InventoryLoaded => State == OrganizationInventoryState.AuthorizedProvisioned;

    // Denied: the authorization gate did not authorize an inventory. The bounded
    // gate tokens flow through verbatim so the wire is byte-identical to the
    // gate-only projection for every production-reachable policy state.
    public static OrganizationInventoryProjection FromDeniedGate(ManagedChefKeysGateProjection gate)
        => new(OrganizationInventoryState.NotAuthorized, gate.WireState, gate.WireReason, 0);

    // Authorized by policy, but no inventory is provisioned (the shipped state).
    public static OrganizationInventoryProjection AuthorizedNotProvisioned()
        => new(OrganizationInventoryState.AuthorizedNotProvisioned,
            "authorized_not_provisioned", "authorized_not_provisioned", 0);

    // Authorized and a valid inventory was PARSED (test-only synthetic path).
    // Even here nothing is resolved, usable, bound, or authorized.
    public static OrganizationInventoryProjection AuthorizedProvisioned(int entryCount)
        => new(OrganizationInventoryState.AuthorizedProvisioned,
            "authorized_provisioned", "provisioned", entryCount < 0 ? 0 : entryCount);

    // Authorized but the source could not be read: fail closed.
    public static OrganizationInventoryProjection Unavailable()
        => new(OrganizationInventoryState.Unavailable, "unavailable", "source_unavailable", 0);

    // Authorized but the source was not trusted: fail closed.
    public static OrganizationInventoryProjection Untrusted()
        => new(OrganizationInventoryState.Untrusted, "untrusted", "source_untrusted", 0);

    // Authorized but the provided document failed validation: fail closed. The
    // reason token is bounded and content-free.
    public static OrganizationInventoryProjection Invalid(OrganizationKeyInventoryInvalidReason reason)
        => new(OrganizationInventoryState.Invalid, "invalid", ReasonToken(reason), 0);

    private static string ReasonToken(OrganizationKeyInventoryInvalidReason reason) => reason switch
    {
        OrganizationKeyInventoryInvalidReason.MalformedJson => "malformed_json",
        OrganizationKeyInventoryInvalidReason.OversizedDocument => "oversized_document",
        OrganizationKeyInventoryInvalidReason.UnsupportedSchema => "unsupported_schema",
        OrganizationKeyInventoryInvalidReason.UnknownField => "unknown_field",
        OrganizationKeyInventoryInvalidReason.ProhibitedField => "prohibited_field",
        OrganizationKeyInventoryInvalidReason.MissingField => "missing_field",
        OrganizationKeyInventoryInvalidReason.WrongType => "wrong_type",
        OrganizationKeyInventoryInvalidReason.EmptyValue => "empty_value",
        OrganizationKeyInventoryInvalidReason.OversizedField => "oversized_field",
        OrganizationKeyInventoryInvalidReason.TooManyEntries => "too_many_entries",
        OrganizationKeyInventoryInvalidReason.InvalidId => "invalid_id",
        OrganizationKeyInventoryInvalidReason.DuplicateId => "duplicate_id",
        OrganizationKeyInventoryInvalidReason.DuplicateProperty => "duplicate_property",
        OrganizationKeyInventoryInvalidReason.InvalidEntries => "invalid_entries",
        OrganizationKeyInventoryInvalidReason.InvalidEncoding => "invalid_encoding",
        OrganizationKeyInventoryInvalidReason.InvalidCertificateReference => "invalid_certificate_reference",
        OrganizationKeyInventoryInvalidReason.DuplicateCertificateReference => "duplicate_certificate_reference",
        _ => "invalid",
    };

    // Content-free: bounded state + reason tokens and the entry count only. Never
    // an identifier, tenant/client reference, certificate datum, secret, or path.
    public override string ToString()
        => $"OrganizationInventoryProjection[state={WireState}, reason={WireReason}, entries={EntryCount}]";
}

// The immutable pairing of the bounded wire projection with the PARSED entries
// that produced it. The entries are the SAME validated, fixed-origin entries the
// parser emitted; they are exposed ONLY so an in-process consumer (the Cycle-11
// resolution coordinator) can resolve per-entry state. They are EMPTY in every
// non-provisioned state, they are never projected onto the wire, and holding one
// grants no capability: `Projection` remains the only boundary-crossing value.
public sealed class OrganizationInventoryEvaluation
{
    private static readonly IReadOnlyList<OrganizationKeyInventoryEntry> EmptyEntries =
        Array.Empty<OrganizationKeyInventoryEntry>();

    private OrganizationInventoryEvaluation(
        OrganizationInventoryProjection projection,
        IReadOnlyList<OrganizationKeyInventoryEntry> entries)
    {
        Projection = projection;
        Entries = entries;
    }

    public OrganizationInventoryProjection Projection { get; }

    public IReadOnlyList<OrganizationKeyInventoryEntry> Entries { get; }

    // Every bounded non-provisioned state: no entry is ever carried.
    internal static OrganizationInventoryEvaluation WithoutEntries(OrganizationInventoryProjection projection)
        => new(projection, EmptyEntries);

    internal static OrganizationInventoryEvaluation Provisioned(
        OrganizationInventoryProjection projection,
        IReadOnlyList<OrganizationKeyInventoryEntry>? entries)
        => new(projection, entries ?? EmptyEntries);

    // Content-free: the projection's own bounded tokens and nothing else.
    public override string ToString() => Projection.ToString();
}

// The pure, deterministic, side-effect-free evaluator. It composes the Cycle 4
// authorization gate, a read-only source, and the bounded parser into a single
// bounded projection. It never touches the registry/filesystem/network/
// certificate store/credential vault itself, never throws through the boundary,
// and enforces the ordering invariant: the source is consulted ONLY when the
// gate authorizes, and never for a denied/unavailable/null gate.
public static class OrganizationKeyInventoryEvaluator
{
    // The bounded wire projection only. This is a thin delegation to
    // EvaluateDetailed, so ordering, short-circuiting, the number of source
    // loads, and the resulting projection are identical by construction.
    public static OrganizationInventoryProjection Evaluate(
        ManagedChefKeysGateProjection? gate,
        IOrganizationKeyInventorySource? source)
        => EvaluateDetailed(gate, source).Projection;

    // The same evaluation, additionally carrying the parsed entries for the
    // provisioned state. The gate still short-circuits FIRST and the source is
    // still consulted at most ONCE and ONLY when the gate authorizes.
    public static OrganizationInventoryEvaluation EvaluateDetailed(
        ManagedChefKeysGateProjection? gate,
        IOrganizationKeyInventorySource? source)
    {
        // Fail closed on a missing gate; the source is NEVER consulted.
        if (gate is null)
        {
            return OrganizationInventoryEvaluation.WithoutEntries(
                OrganizationInventoryProjection.Unavailable());
        }

        // Gate short-circuit: a denied/unavailable gate carries its bounded tokens
        // straight through and the source is NEVER invoked.
        if (!gate.Authorized)
        {
            return OrganizationInventoryEvaluation.WithoutEntries(
                OrganizationInventoryProjection.FromDeniedGate(gate));
        }

        // Authorized but no source wired: fail closed, never throw.
        if (source is null)
        {
            return OrganizationInventoryEvaluation.WithoutEntries(
                OrganizationInventoryProjection.Unavailable());
        }

        OrganizationInventorySourceResult result = source.Load();
        switch (result.Kind)
        {
            case OrganizationInventorySourceKind.NotProvisioned:
                return OrganizationInventoryEvaluation.WithoutEntries(
                    OrganizationInventoryProjection.AuthorizedNotProvisioned());

            case OrganizationInventorySourceKind.Unavailable:
                return OrganizationInventoryEvaluation.WithoutEntries(
                    OrganizationInventoryProjection.Unavailable());

            case OrganizationInventorySourceKind.Untrusted:
                return OrganizationInventoryEvaluation.WithoutEntries(
                    OrganizationInventoryProjection.Untrusted());

            case OrganizationInventorySourceKind.Provided:
                OrganizationKeyInventoryParseResult parsedResult =
                    OrganizationKeyInventoryParser.Parse(result.Document);
                return parsedResult.IsValid
                    ? OrganizationInventoryEvaluation.Provisioned(
                        OrganizationInventoryProjection.AuthorizedProvisioned(parsedResult.EntryCount),
                        parsedResult.Entries)
                    : OrganizationInventoryEvaluation.WithoutEntries(
                        OrganizationInventoryProjection.Invalid(parsedResult.Reason));

            case OrganizationInventorySourceKind.InvalidContent:
                // A trusted object with undecodable bytes fails closed to invalid
                // WITHOUT invoking the parser (there are no decodable bytes).
                return OrganizationInventoryEvaluation.WithoutEntries(
                    OrganizationInventoryProjection.Invalid(
                        OrganizationKeyInventoryInvalidReason.InvalidEncoding));

            default:
                return OrganizationInventoryEvaluation.WithoutEntries(
                    OrganizationInventoryProjection.Unavailable());
        }
    }
}
