using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PAXCookbookSetup.Payload;

internal enum InternalLocalPayloadState
{
    Prepared,
    InvalidRequest,
    ZipUnavailable,
    ZipMismatch,
    DestinationExists,
    ArchiveRefused,
    ManifestRefused,
    PayloadMismatch,
    ProvenanceWriteFailed,
    CleanupFailed
}

internal sealed record InternalLocalPayloadRequest(
    string ZipPath,
    string ExpectedSha256,
    long ExpectedLength,
    string CycleStateRoot);

internal sealed class InternalLocalPayloadResult
{
    private static readonly IReadOnlyList<string> EmptyArguments =
        Array.AsReadOnly(Array.Empty<string>());

    private InternalLocalPayloadResult(
        InternalLocalPayloadState state,
        string? validatedPayloadRoot = null,
        string? provenancePath = null,
        int memberCount = 0,
        string? aggregateSha256 = null,
        IReadOnlyList<string>? installArguments = null,
        IReadOnlyList<string>? uninstallArguments = null)
    {
        State = state;
        ValidatedPayloadRoot = validatedPayloadRoot;
        ProvenancePath = provenancePath;
        MemberCount = memberCount;
        AggregateSha256 = aggregateSha256;
        InstallArguments = installArguments ?? EmptyArguments;
        UninstallArguments = uninstallArguments ?? EmptyArguments;
    }

    internal InternalLocalPayloadState State { get; }
    internal bool Success => State == InternalLocalPayloadState.Prepared;
    internal string? ValidatedPayloadRoot { get; }
    internal string? ProvenancePath { get; }
    internal int MemberCount { get; }
    internal string? AggregateSha256 { get; }
    internal IReadOnlyList<string> InstallArguments { get; }
    internal IReadOnlyList<string> UninstallArguments { get; }

    internal static InternalLocalPayloadResult Refuse(InternalLocalPayloadState state) => new(state);

    internal static InternalLocalPayloadResult Prepared(
        string payloadRoot,
        string provenancePath,
        int memberCount,
        string aggregateSha256)
    {
        ReadOnlyCollection<string> install = Array.AsReadOnly(new[]
        {
            "install", "--silent", "--payload-root", payloadRoot
        });
        ReadOnlyCollection<string> uninstall = Array.AsReadOnly(new[]
        {
            "uninstall", "--silent"
        });
        return new InternalLocalPayloadResult(
            InternalLocalPayloadState.Prepared,
            payloadRoot,
            provenancePath,
            memberCount,
            aggregateSha256,
            install,
            uninstall);
    }
}

internal static class InternalLocalPayloadContract
{
    private const string ManifestName = "manifest.json";
    private const string PayloadRootPrefix = "PAXCookbookPayload_";
    private const string ProvenanceSuffix = ".provenance.json";
    private const string ClaimSuffix = ".claim";

    internal static InternalLocalPayloadResult Prepare(InternalLocalPayloadRequest request)
    {
        if (!TryValidateRequest(request, out string zipPath, out string cycleStateRoot))
        {
            return InternalLocalPayloadResult.Refuse(InternalLocalPayloadState.InvalidRequest);
        }
        if (!File.Exists(zipPath))
        {
            return InternalLocalPayloadResult.Refuse(InternalLocalPayloadState.ZipUnavailable);
        }

        try
        {
            using FileStream zipStream = new(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (zipStream.Length != request.ExpectedLength)
            {
                return InternalLocalPayloadResult.Refuse(InternalLocalPayloadState.ZipMismatch);
            }
            string zipSha256 = HashStream(zipStream);
            if (!string.Equals(zipSha256, request.ExpectedSha256, StringComparison.Ordinal))
            {
                return InternalLocalPayloadResult.Refuse(InternalLocalPayloadState.ZipMismatch);
            }
            zipStream.Position = 0;

            using ZipArchive archive = new(zipStream, ZipArchiveMode.Read, leaveOpen: true);
            if (!TryIndexArchive(archive, out ArchiveIndex? archiveIndex))
            {
                return InternalLocalPayloadResult.Refuse(InternalLocalPayloadState.ArchiveRefused);
            }
            if (!TryReadManifest(archiveIndex!, out ParsedManifest? manifest))
            {
                return InternalLocalPayloadResult.Refuse(InternalLocalPayloadState.ManifestRefused);
            }
            InternalLocalPayloadState membershipState = BindMembership(
                archiveIndex!,
                manifest!,
                out IReadOnlyList<ManifestMember>? members);
            if (membershipState != InternalLocalPayloadState.Prepared)
            {
                return InternalLocalPayloadResult.Refuse(membershipState);
            }

            Directory.CreateDirectory(cycleStateRoot);
            string payloadRoot = Path.GetFullPath(Path.Combine(
                cycleStateRoot,
                PayloadRootPrefix + zipSha256));
            string provenancePath = payloadRoot + ProvenanceSuffix;
            string claimPath = payloadRoot + ClaimSuffix;
            if (Directory.Exists(payloadRoot) || File.Exists(provenancePath) || File.Exists(claimPath))
            {
                return InternalLocalPayloadResult.Refuse(InternalLocalPayloadState.DestinationExists);
            }

            try
            {
                using FileStream claim = new(
                    claimPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                claim.Flush(flushToDisk: true);
            }
            catch
            {
                return InternalLocalPayloadResult.Refuse(InternalLocalPayloadState.DestinationExists);
            }

            InternalLocalPayloadState extractionState = ExtractAndValidate(
                archiveIndex!,
                members!,
                payloadRoot);
            if (extractionState != InternalLocalPayloadState.Prepared)
            {
                return RefuseAndClean(extractionState, payloadRoot, claimPath);
            }

            string aggregate = ComputeAggregateSha256(members!);
            try
            {
                WriteProvenance(
                    provenancePath,
                    zipSha256,
                    zipStream.Length,
                    payloadRoot,
                    members!.Count,
                    aggregate);
                File.Delete(claimPath);
            }
            catch
            {
                TryDeleteFile(provenancePath);
                return RefuseAndClean(
                    InternalLocalPayloadState.ProvenanceWriteFailed,
                    payloadRoot,
                    claimPath);
            }

            return InternalLocalPayloadResult.Prepared(
                payloadRoot,
                provenancePath,
                members!.Count,
                aggregate);
        }
        catch
        {
            return InternalLocalPayloadResult.Refuse(InternalLocalPayloadState.ArchiveRefused);
        }
    }

    private static bool TryValidateRequest(
        InternalLocalPayloadRequest? request,
        out string zipPath,
        out string cycleStateRoot)
    {
        zipPath = string.Empty;
        cycleStateRoot = string.Empty;
        if (request is null
            || string.IsNullOrWhiteSpace(request.ZipPath)
            || string.IsNullOrWhiteSpace(request.CycleStateRoot)
            || request.ExpectedLength < 0
            || !IsUpperSha256(request.ExpectedSha256))
        {
            return false;
        }
        try
        {
            if (!Path.IsPathFullyQualified(request.ZipPath)
                || !Path.IsPathFullyQualified(request.CycleStateRoot))
            {
                return false;
            }
            zipPath = Path.GetFullPath(request.ZipPath);
            cycleStateRoot = Path.GetFullPath(request.CycleStateRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return cycleStateRoot.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryIndexArchive(ZipArchive archive, out ArchiveIndex? index)
    {
        index = null;
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (!TryNormalizeRelativePath(entry.FullName, out string normalized, allowDirectory: true))
            {
                return false;
            }
            bool isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal)
                || entry.FullName.EndsWith("\\", StringComparison.Ordinal);
            if (isDirectory)
            {
                if (!directories.Add(normalized) || files.ContainsKey(normalized)) return false;
                continue;
            }
            if (!files.TryAdd(normalized, entry) || directories.Contains(normalized)) return false;
        }
        if (!files.TryGetValue(ManifestName, out ZipArchiveEntry? manifestEntry)
            || !string.Equals(NormalizeSeparators(manifestEntry.FullName), ManifestName, StringComparison.Ordinal))
        {
            return false;
        }
        index = new ArchiveIndex(files, directories, manifestEntry);
        return true;
    }

    private static bool TryReadManifest(ArchiveIndex archive, out ParsedManifest? manifest)
    {
        manifest = null;
        try
        {
            using Stream stream = archive.ManifestEntry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            using JsonDocument document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            JsonElement root = document.RootElement;
            if (!TryObject(
                    root,
                    new[]
                    {
                        "product", "manifestSchemaVersion", "appVersion", "setupVersion", "buildId",
                        "builtAtUtc", "channel", "targetOs", "targetArch", "webView2RuntimeRequirement",
                        "payload"
                    },
                    new[] { "minWindowsBuild", "signature" },
                    out Dictionary<string, JsonElement>? top))
            {
                return false;
            }
            if (!TryExactString(top!, "product", "PAXCookbook")
                || !TryExactInt32(top!, "manifestSchemaVersion", 1)
                || !TryNonEmptyString(top!, "appVersion")
                || !TryNonEmptyString(top!, "setupVersion")
                || !TryString(top!, "buildId", out _)
                || !TryNonEmptyString(top!, "builtAtUtc")
                || !TryNonEmptyString(top!, "channel")
                || !TryExactString(top!, "targetOs", "windows")
                || !TryExactString(top!, "targetArch", "x64")
                || !TryNullableInt32(top!, "minWindowsBuild")
                || !TryWebViewRequirement(top!["webView2RuntimeRequirement"])
                || !TrySignature(top!, "signature"))
            {
                return false;
            }

            if (!TryObject(
                    top!["payload"],
                    new[] { "setupExe", "appExe", "files" },
                    Array.Empty<string>(),
                    out Dictionary<string, JsonElement>? payload))
            {
                return false;
            }
            if (!TrySetupExe(payload!["setupExe"], out ManifestMember? setup)
                || !TryAppExe(payload["appExe"], out ManifestMember? app)
                || payload["files"].ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var files = new List<ManifestMember>();
            foreach (JsonElement element in payload["files"].EnumerateArray())
            {
                if (!TryFile(element, out ManifestMember? file)) return false;
                files.Add(file!);
            }
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName };
            if (!paths.Add(app!.Path)) return false;
            foreach (ManifestMember file in files)
            {
                if (!paths.Add(file.Path)) return false;
            }
            if (string.Equals(setup!.Path, ManifestName, StringComparison.OrdinalIgnoreCase)) return false;
            manifest = new ParsedManifest(setup, app, files.AsReadOnly());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetupExe(JsonElement element, out ManifestMember? member)
    {
        member = null;
        if (!TryObject(
                element,
                new[] { "name", "sha256", "sizeBytes" },
                new[] { "note" },
                out Dictionary<string, JsonElement>? values)
            || !TryOptionalString(values!, "note"))
        {
            return false;
        }
        return TryMember(values!, "name", out member, requireLeaf: true);
    }

    private static bool TryAppExe(JsonElement element, out ManifestMember? member)
    {
        member = null;
        if (!TryObject(
                element,
                new[] { "name", "sha256", "sizeBytes", "relativeInstallPath" },
                Array.Empty<string>(),
                out Dictionary<string, JsonElement>? values)
            || !TryNonEmptyString(values!, "name"))
        {
            return false;
        }
        return TryMember(values!, "relativeInstallPath", out member, requireLeaf: false);
    }

    private static bool TryFile(JsonElement element, out ManifestMember? member)
    {
        member = null;
        if (!TryObject(
                element,
                new[] { "relativeInstallPath", "sha256", "sizeBytes" },
                Array.Empty<string>(),
                out Dictionary<string, JsonElement>? values))
        {
            return false;
        }
        return TryMember(values!, "relativeInstallPath", out member, requireLeaf: false);
    }

    private static bool TryMember(
        Dictionary<string, JsonElement> values,
        string pathProperty,
        out ManifestMember? member,
        bool requireLeaf)
    {
        member = null;
        if (!TryString(values, pathProperty, out string path)
            || !TryNormalizeRelativePath(path, out string normalized, allowDirectory: false)
            || (requireLeaf && normalized.Contains('/', StringComparison.Ordinal))
            || !TryString(values, "sha256", out string sha256)
            || !IsSha256(sha256)
            || !TryInt64(values, "sizeBytes", out long size)
            || size < 0)
        {
            return false;
        }
        member = new ManifestMember(normalized, sha256.ToUpperInvariant(), size);
        return true;
    }

    private static InternalLocalPayloadState BindMembership(
        ArchiveIndex archive,
        ParsedManifest manifest,
        out IReadOnlyList<ManifestMember>? members)
    {
        members = null;
        var expected = new Dictionary<string, ManifestMember>(StringComparer.OrdinalIgnoreCase)
        {
            [manifest.App.Path] = manifest.App
        };
        foreach (ManifestMember file in manifest.Files)
        {
            if (!expected.TryAdd(file.Path, file)) return InternalLocalPayloadState.ManifestRefused;
        }
        if (archive.Files.ContainsKey(manifest.Setup.Path)
            && !expected.TryAdd(manifest.Setup.Path, manifest.Setup))
        {
            return InternalLocalPayloadState.ManifestRefused;
        }

        string[] expectedFilePaths = expected.Keys.Append(ManifestName).ToArray();
        foreach (string directory in archive.Directories)
        {
            string prefix = directory + "/";
            if (!expectedFilePaths.Any(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                return InternalLocalPayloadState.ArchiveRefused;
            }
        }

        foreach (string actual in archive.Files.Keys)
        {
            if (string.Equals(actual, ManifestName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!expected.ContainsKey(actual)) return InternalLocalPayloadState.ArchiveRefused;
        }
        foreach (ManifestMember expectedMember in expected.Values)
        {
            if (!archive.Files.TryGetValue(expectedMember.Path, out ZipArchiveEntry? entry)
                || entry.Length != expectedMember.Size
                || !EntryHashEquals(entry, expectedMember.Sha256))
            {
                return InternalLocalPayloadState.PayloadMismatch;
            }
        }
        members = expected.Values
            .OrderBy(member => member.Path, StringComparer.Ordinal)
            .ToArray();
        return InternalLocalPayloadState.Prepared;
    }

    private static InternalLocalPayloadState ExtractAndValidate(
        ArchiveIndex archive,
        IReadOnlyList<ManifestMember> members,
        string payloadRoot)
    {
        try
        {
            Directory.CreateDirectory(payloadRoot);
            string rootPrefix = payloadRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string directory in archive.Directories.OrderBy(path => path, StringComparer.Ordinal))
            {
                string target = CombineUnderRoot(payloadRoot, rootPrefix, directory);
                Directory.CreateDirectory(target);
            }
            foreach ((string relative, ZipArchiveEntry entry) in archive.Files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                string target = CombineUnderRoot(payloadRoot, rootPrefix, relative);
                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                using FileStream destination = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using Stream source = entry.Open();
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            string[] diskFiles = Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories);
            var diskPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string diskFile in diskFiles)
            {
                string relative = NormalizeSeparators(Path.GetRelativePath(payloadRoot, diskFile));
                if (!diskPaths.Add(relative)) return InternalLocalPayloadState.PayloadMismatch;
            }
            var expectedPaths = new HashSet<string>(
                members.Select(member => member.Path).Append(ManifestName),
                StringComparer.OrdinalIgnoreCase);
            if (!diskPaths.SetEquals(expectedPaths)) return InternalLocalPayloadState.PayloadMismatch;
            foreach (ManifestMember member in members)
            {
                string path = CombineUnderRoot(payloadRoot, rootPrefix, member.Path);
                if (new FileInfo(path).Length != member.Size
                    || !string.Equals(HashFile(path), member.Sha256, StringComparison.Ordinal))
                {
                    return InternalLocalPayloadState.PayloadMismatch;
                }
            }
            return InternalLocalPayloadState.Prepared;
        }
        catch
        {
            return InternalLocalPayloadState.ArchiveRefused;
        }
    }

    private static string ComputeAggregateSha256(IReadOnlyList<ManifestMember> members)
    {
        using var canonical = new MemoryStream();
        foreach (ManifestMember member in members.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            byte[] row = Encoding.UTF8.GetBytes(
                member.Path
                + '\0'
                + member.Size.ToString(CultureInfo.InvariantCulture)
                + '\0'
                + member.Sha256
                + "\n");
            canonical.Write(row);
        }
        return Convert.ToHexString(SHA256.HashData(canonical.ToArray()));
    }

    private static void WriteProvenance(
        string path,
        string zipSha256,
        long zipLength,
        string extractionRoot,
        int memberCount,
        string aggregateSha256)
    {
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("zipSha256", zipSha256);
            writer.WriteNumber("zipLength", zipLength);
            writer.WriteString("extractionRoot", extractionRoot);
            writer.WriteNumber("memberCount", memberCount);
            writer.WriteString("aggregateSha256", aggregateSha256);
            writer.WriteEndObject();
            writer.Flush();
        }
        stream.Flush(flushToDisk: true);
    }

    private static InternalLocalPayloadResult RefuseAndClean(
        InternalLocalPayloadState state,
        string payloadRoot,
        string claimPath)
    {
        bool cleaned = true;
        try
        {
            if (Directory.Exists(payloadRoot)) Directory.Delete(payloadRoot, recursive: true);
        }
        catch
        {
            cleaned = false;
        }
        if (!TryDeleteFile(claimPath)) cleaned = false;
        return InternalLocalPayloadResult.Refuse(
            cleaned ? state : InternalLocalPayloadState.CleanupFailed);
    }

    private static bool TryObject(
        JsonElement element,
        IReadOnlyList<string> required,
        IReadOnlyList<string> optional,
        out Dictionary<string, JsonElement>? values)
    {
        values = null;
        if (element.ValueKind != JsonValueKind.Object) return false;
        var allowed = new HashSet<string>(required, StringComparer.Ordinal);
        allowed.UnionWith(optional);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !result.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }
        if (required.Any(name => !result.ContainsKey(name))) return false;
        values = result;
        return true;
    }

    private static bool TryWebViewRequirement(JsonElement element)
    {
        if (!TryObject(
                element,
                new[] { "minimumPv", "detectionPaths" },
                Array.Empty<string>(),
                out Dictionary<string, JsonElement>? values)
            || !TryNonEmptyString(values!, "minimumPv")
            || values!["detectionPaths"].ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        return values["detectionPaths"].EnumerateArray()
            .All(item => item.ValueKind == JsonValueKind.String);
    }

    private static bool TrySignature(Dictionary<string, JsonElement> values, string name)
    {
        if (!values.TryGetValue(name, out JsonElement signature)
            || signature.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        return TryObject(
                signature,
                new[] { "algorithm", "value" },
                Array.Empty<string>(),
                out Dictionary<string, JsonElement>? fields)
            && TryNonEmptyString(fields!, "algorithm")
            && TryString(fields!, "value", out _);
    }

    private static bool TryNullableInt32(Dictionary<string, JsonElement> values, string name)
    {
        if (!values.TryGetValue(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _);
    }

    private static bool TryOptionalString(Dictionary<string, JsonElement> values, string name) =>
        !values.TryGetValue(name, out JsonElement value) || value.ValueKind == JsonValueKind.String;

    private static bool TryExactString(
        Dictionary<string, JsonElement> values,
        string name,
        string expected) =>
        TryString(values, name, out string actual)
        && string.Equals(actual, expected, StringComparison.Ordinal);

    private static bool TryExactInt32(
        Dictionary<string, JsonElement> values,
        string name,
        int expected) =>
        values.TryGetValue(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int actual)
        && actual == expected;

    private static bool TryNonEmptyString(Dictionary<string, JsonElement> values, string name) =>
        TryString(values, name, out string value) && !string.IsNullOrWhiteSpace(value);

    private static bool TryString(
        Dictionary<string, JsonElement> values,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!values.TryGetValue(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryInt64(
        Dictionary<string, JsonElement> values,
        string name,
        out long value)
    {
        value = 0;
        return values.TryGetValue(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value);
    }

    private static bool TryNormalizeRelativePath(
        string? value,
        out string normalized,
        bool allowDirectory)
    {
        normalized = string.Empty;
        if (string.IsNullOrEmpty(value)
            || value.Any(char.IsControl)
            || Path.IsPathRooted(value)
            || value.Contains(":", StringComparison.Ordinal)
            || value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("\\", StringComparison.Ordinal))
        {
            return false;
        }
        bool directory = value.EndsWith("/", StringComparison.Ordinal)
            || value.EndsWith("\\", StringComparison.Ordinal);
        if (directory && !allowDirectory) return false;
        string candidate = NormalizeSeparators(value);
        if (directory) candidate = candidate.TrimEnd('/');
        string[] segments = candidate.Split('/');
        if (segments.Length == 0
            || segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return false;
        }
        normalized = string.Join('/', segments);
        return normalized.Length > 0;
    }

    private static string CombineUnderRoot(string root, string rootPrefix, string relative)
    {
        string combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException();
        }
        return combined;
    }

    private static bool EntryHashEquals(ZipArchiveEntry entry, string expected)
    {
        using Stream stream = entry.Open();
        return string.Equals(HashStream(stream), expected, StringComparison.Ordinal);
    }

    private static string HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return HashStream(stream);
    }

    private static string HashStream(Stream stream)
    {
        if (stream.CanSeek) stream.Position = 0;
        string result = Convert.ToHexString(SHA256.HashData(stream));
        if (stream.CanSeek) stream.Position = 0;
        return result;
    }

    private static bool IsUpperSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9'
            or >= 'A' and <= 'F'
            or >= 'a' and <= 'f');

    private static string NormalizeSeparators(string value) => value.Replace('\\', '/');

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private sealed record ManifestMember(string Path, string Sha256, long Size);

    private sealed record ParsedManifest(
        ManifestMember Setup,
        ManifestMember App,
        IReadOnlyList<ManifestMember> Files);

    private sealed record ArchiveIndex(
        IReadOnlyDictionary<string, ZipArchiveEntry> Files,
        IReadOnlySet<string> Directories,
        ZipArchiveEntry ManifestEntry);
}