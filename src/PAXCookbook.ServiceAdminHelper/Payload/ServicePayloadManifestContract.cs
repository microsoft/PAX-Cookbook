// PAX Cookbook - SERVICE PAYLOAD INNER MANIFEST CONTRACT (cycle 61)
//
// SECURITY CLASSIFICATION. This manifest is a CORRUPTION/CONSISTENCY contract
// only. It ships inside the archive it describes, inside an UNSIGNED PRERELEASE
// helper, so a valid manifest proves nothing about an adversary. The outer
// Authenticode signature is the trust anchor and it does not exist yet; GA
// activation stays BLOCKED until this exact helper is signed and the expected
// publisher policy is configured and verified.
//
// WHAT THIS FILE IS. A pure, closed schema validator over UTF-8 bytes. It
// performs NO file access, NO extraction, NO write, NO process start, NO
// registry, certificate, key or credential access, and NO network access. It
// returns a bounded outcome and, on success, the declared member list.
//
// PRIVACY - FAIL CLOSED. No outcome carries JSON text, an exception message, a
// path fragment or a hash. ToString() carries only the bounded token.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace PAXCookbook.ServiceAdminHelper.Payload;

/// <summary>
/// Bounded outcome of inner-manifest validation. Zero is the permanent, safe
/// default so an uninitialised value can never read as valid.
/// </summary>
internal enum ServicePayloadManifestOutcome
{
    Unspecified = 0,
    Valid = 1,

    /// <summary>Not UTF-8 without BOM, contains CR, or is not parseable JSON.</summary>
    Unreadable = 2,

    /// <summary>The document root is not a JSON object.</summary>
    NotJsonObject = 3,

    /// <summary>A property outside the closed top-level allow-list is present.</summary>
    UnknownProperty = 4,

    /// <summary>A top-level property name appears more than once.</summary>
    DuplicateProperty = 5,

    /// <summary>A required top-level property is absent.</summary>
    MissingProperty = 6,

    UnsupportedSchemaVersion = 7,
    UnsupportedTargetOs = 8,
    UnsupportedTargetArch = 9,

    /// <summary>The files property is not a JSON array.</summary>
    FilesNotArray = 10,

    /// <summary>The files array is empty.</summary>
    FilesEmpty = 11,

    FileEntryNotObject = 12,
    FileEntryUnknownProperty = 13,
    FileEntryDuplicateProperty = 14,
    FileEntryMissingProperty = 15,

    /// <summary>A declared name is absolute, rooted, parent-relative, empty, or uses an alternate separator.</summary>
    UnsafePath = 16,

    /// <summary>Two declared names are equal, or collide case-insensitively.</summary>
    DuplicatePath = 17,

    /// <summary>A declared size is negative, non-integral, or not a JSON number.</summary>
    InvalidSize = 18,

    /// <summary>A declared hash is not exactly 64 uppercase hexadecimal characters.</summary>
    InvalidHash = 19,

    /// <summary>A declared name carries a prohibited extension or a prohibited exact leaf name.</summary>
    ProhibitedPath = 20,
}

/// <summary>One declared archive member. Carries no capability.</summary>
internal readonly struct ServicePayloadManifestFileEntry
{
    internal ServicePayloadManifestFileEntry(string name, long sizeBytes, string sha256)
    {
        Name = name;
        SizeBytes = sizeBytes;
        Sha256 = sha256;
    }

    internal string Name { get; }

    internal long SizeBytes { get; }

    /// <summary>Uppercase 64-character SHA-256 of the member bytes.</summary>
    internal string Sha256 { get; }

    /// <summary>Carries the bounded type name only - never the name, size or hash.</summary>
    public override string ToString() => nameof(ServicePayloadManifestFileEntry);
}

/// <summary>
/// The manifest validation result. <see cref="Files"/> is non-empty only when
/// <see cref="Outcome"/> is <see cref="ServicePayloadManifestOutcome.Valid"/>.
/// </summary>
internal readonly struct ServicePayloadManifestResult
{
    private ServicePayloadManifestResult(
        ServicePayloadManifestOutcome outcome, IReadOnlyList<ServicePayloadManifestFileEntry> files)
    {
        Outcome = outcome;
        Files = files;
    }

    internal ServicePayloadManifestOutcome Outcome { get; }

    internal IReadOnlyList<ServicePayloadManifestFileEntry> Files { get; }

    internal bool IsValid => Outcome == ServicePayloadManifestOutcome.Valid;

    internal static ServicePayloadManifestResult Valid(IReadOnlyList<ServicePayloadManifestFileEntry> files) =>
        new(ServicePayloadManifestOutcome.Valid, files);

    internal static ServicePayloadManifestResult Refused(ServicePayloadManifestOutcome outcome) =>
        new(
            outcome == ServicePayloadManifestOutcome.Valid ? ServicePayloadManifestOutcome.Unreadable : outcome,
            Array.Empty<ServicePayloadManifestFileEntry>());

    /// <summary>Carries the bounded outcome token only.</summary>
    public override string ToString() => Outcome.ToString();
}

/// <summary>
/// The closed inner-manifest schema. The manifest is the ONLY permitted
/// non-runtime member of the payload archive.
/// </summary>
internal static class ServicePayloadManifestContract
{
    /// <summary>The closed top-level property allow-list, in canonical order.</summary>
    internal static IReadOnlyList<string> TopLevelPropertyNames { get; } = new[]
    {
        "schemaVersion",
        "targetOs",
        "targetArch",
        "files",
    };

    /// <summary>The closed file-entry property allow-list, in canonical order.</summary>
    internal static IReadOnlyList<string> FileEntryPropertyNames { get; } = new[]
    {
        "name",
        "sizeBytes",
        "sha256",
    };

    /// <summary>
    /// Validate manifest bytes against the closed schema. Pure: no file access,
    /// no extraction, no write, no process start. Every failure is a bounded
    /// token that carries no document text.
    /// </summary>
    internal static ServicePayloadManifestResult Parse(ReadOnlySpan<byte> utf8Bytes)
    {
        string text;
        try
        {
            if (utf8Bytes.Length >= 3 && utf8Bytes[0] == 0xEF && utf8Bytes[1] == 0xBB && utf8Bytes[2] == 0xBF)
            {
                return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.Unreadable);
            }

            foreach (byte b in utf8Bytes)
            {
                if (b == 0x0D)
                {
                    return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.Unreadable);
                }
            }

            // CANONICAL, ESCAPE-FREE SERIALIZATION. No legal value in this
            // closed schema needs a JSON escape: the schema version is a number,
            // the OS and architecture are fixed literals, a hash is hexadecimal,
            // and a name may contain only safe relative-path characters
            // separated by '/'. A backslash byte can therefore only be an
            // alternate path separator or an escape sequence, and both are
            // refused as an unsafe path rather than parsed and then judged.
            foreach (byte b in utf8Bytes)
            {
                if (b == 0x5C)
                {
                    return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.UnsafePath);
                }
            }

            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(utf8Bytes);
        }
        catch (Exception)
        {
            return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.Unreadable);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return Validate(document.RootElement);
        }
        catch (JsonException)
        {
            return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.Unreadable);
        }
        catch (Exception)
        {
            return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.Unreadable);
        }
    }

    private static ServicePayloadManifestResult Validate(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.NotJsonObject);
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!Contains(TopLevelPropertyNames, property.Name))
            {
                return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.UnknownProperty);
            }

            seen[property.Name] = seen.TryGetValue(property.Name, out int count) ? count + 1 : 1;
        }

        foreach (KeyValuePair<string, int> entry in seen)
        {
            if (entry.Value > 1)
            {
                return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.DuplicateProperty);
            }
        }

        foreach (string required in TopLevelPropertyNames)
        {
            if (!seen.ContainsKey(required))
            {
                return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.MissingProperty);
            }
        }

        JsonElement schemaVersion = root.GetProperty("schemaVersion");
        if (schemaVersion.ValueKind != JsonValueKind.Number
            || !schemaVersion.TryGetInt32(out int schemaVersionValue)
            || schemaVersionValue != ServicePayloadArchiveFormat.SchemaVersion)
        {
            return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.UnsupportedSchemaVersion);
        }

        JsonElement targetOs = root.GetProperty("targetOs");
        if (targetOs.ValueKind != JsonValueKind.String
            || !string.Equals(targetOs.GetString(), ServicePayloadArchiveFormat.TargetOs, StringComparison.Ordinal))
        {
            return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.UnsupportedTargetOs);
        }

        JsonElement targetArch = root.GetProperty("targetArch");
        if (targetArch.ValueKind != JsonValueKind.String
            || !string.Equals(targetArch.GetString(), ServicePayloadArchiveFormat.TargetArch, StringComparison.Ordinal))
        {
            return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.UnsupportedTargetArch);
        }

        JsonElement files = root.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array)
        {
            return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.FilesNotArray);
        }

        var declared = new List<ServicePayloadManifestFileEntry>();
        foreach (JsonElement element in files.EnumerateArray())
        {
            ServicePayloadManifestOutcome failure = ReadFileEntry(element, out ServicePayloadManifestFileEntry entry);
            if (failure != ServicePayloadManifestOutcome.Valid)
            {
                return ServicePayloadManifestResult.Refused(failure);
            }

            declared.Add(entry);
        }

        if (declared.Count == 0)
        {
            return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.FilesEmpty);
        }

        var exact = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ServicePayloadManifestFileEntry entry in declared)
        {
            if (!exact.Add(entry.Name) || !caseInsensitive.Add(entry.Name))
            {
                return ServicePayloadManifestResult.Refused(ServicePayloadManifestOutcome.DuplicatePath);
            }
        }

        return ServicePayloadManifestResult.Valid(declared);
    }

    private static ServicePayloadManifestOutcome ReadFileEntry(
        JsonElement element, out ServicePayloadManifestFileEntry entry)
    {
        entry = default;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return ServicePayloadManifestOutcome.FileEntryNotObject;
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!Contains(FileEntryPropertyNames, property.Name))
            {
                return ServicePayloadManifestOutcome.FileEntryUnknownProperty;
            }

            seen[property.Name] = seen.TryGetValue(property.Name, out int count) ? count + 1 : 1;
        }

        foreach (KeyValuePair<string, int> pair in seen)
        {
            if (pair.Value > 1)
            {
                return ServicePayloadManifestOutcome.FileEntryDuplicateProperty;
            }
        }

        foreach (string required in FileEntryPropertyNames)
        {
            if (!seen.ContainsKey(required))
            {
                return ServicePayloadManifestOutcome.FileEntryMissingProperty;
            }
        }

        JsonElement nameElement = element.GetProperty("name");
        if (nameElement.ValueKind != JsonValueKind.String)
        {
            return ServicePayloadManifestOutcome.UnsafePath;
        }

        string name = nameElement.GetString() ?? string.Empty;
        if (!ServicePayloadArchiveFormat.IsSafeRelativeName(name))
        {
            return ServicePayloadManifestOutcome.UnsafePath;
        }

        if (ServicePayloadArchiveFormat.IsProhibitedMemberName(name))
        {
            return ServicePayloadManifestOutcome.ProhibitedPath;
        }

        JsonElement sizeElement = element.GetProperty("sizeBytes");
        if (sizeElement.ValueKind != JsonValueKind.Number
            || !sizeElement.TryGetInt64(out long size)
            || size < 0)
        {
            return ServicePayloadManifestOutcome.InvalidSize;
        }

        JsonElement hashElement = element.GetProperty("sha256");
        if (hashElement.ValueKind != JsonValueKind.String)
        {
            return ServicePayloadManifestOutcome.InvalidHash;
        }

        string hash = hashElement.GetString() ?? string.Empty;
        if (!IsUppercaseSha256(hash))
        {
            return ServicePayloadManifestOutcome.InvalidHash;
        }

        entry = new ServicePayloadManifestFileEntry(name, size, hash);
        return ServicePayloadManifestOutcome.Valid;
    }

    /// <summary>Exactly 64 uppercase hexadecimal characters. Lowercase is refused.</summary>
    internal static bool IsUppercaseSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool digit = c >= '0' && c <= '9';
            bool upperHex = c >= 'A' && c <= 'F';
            if (!digit && !upperHex)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Contains(IReadOnlyList<string> allowList, string candidate)
    {
        foreach (string allowed in allowList)
        {
            if (string.Equals(allowed, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
