using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PAXCookbookSetup.Payload;
using Xunit;

namespace PAXCookbookSetup.Tests;

public sealed class InternalLocalPayloadContractTests
{
    private const string AppPath = "App/bin/PAX Cookbook.exe";
    private const string SupportPath = "Setup/PAXCookbookSetup.dll";
    private const string ExpectedAggregate = "31DA0E02232B1CF0B743E2EAC4A2C98B57D8345CD9A9B0BED113B0C4D6D5DD68";

    [Fact]
    public void PinnedZip_PreparesShaNamedRootAndProvenance()
    {
        string temp = NewTempRoot();
        try
        {
            ZipFixture fixture = BuildFixture(temp);
            InternalLocalPayloadResult result = Prepare(fixture, Path.Combine(temp, "state"));

            Assert.True(result.Success);
            Assert.Equal(InternalLocalPayloadState.Prepared, result.State);
            string expectedRoot = Path.Combine(temp, "state", "PAXCookbookPayload_" + fixture.Sha256);
            Assert.Equal(Path.GetFullPath(expectedRoot), result.ValidatedPayloadRoot);
            Assert.True(Directory.Exists(expectedRoot));
            Assert.Equal(2, result.MemberCount);
            Assert.Equal(ExpectedAggregate, result.AggregateSha256);

            string expectedProvenance = expectedRoot + ".provenance.json";
            Assert.Equal(expectedProvenance, result.ProvenancePath);
            using JsonDocument provenance = JsonDocument.Parse(File.ReadAllText(expectedProvenance));
            Assert.Equal(1, provenance.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(fixture.Sha256, provenance.RootElement.GetProperty("zipSha256").GetString());
            Assert.Equal(fixture.Length, provenance.RootElement.GetProperty("zipLength").GetInt64());
            Assert.Equal(Path.GetFullPath(expectedRoot), provenance.RootElement.GetProperty("extractionRoot").GetString());
            Assert.Equal(2, provenance.RootElement.GetProperty("memberCount").GetInt32());
            Assert.Equal(ExpectedAggregate, provenance.RootElement.GetProperty("aggregateSha256").GetString());
            Assert.Equal(6, provenance.RootElement.EnumerateObject().Count());

            string marker = Path.Combine(expectedRoot, "existing.marker");
            File.WriteAllText(marker, "preserve");
            InternalLocalPayloadResult existing = Prepare(fixture, Path.Combine(temp, "state"));
            Assert.Equal(InternalLocalPayloadState.DestinationExists, existing.State);
            Assert.Equal("preserve", File.ReadAllText(marker));
        }
        finally
        {
            TryRemove(temp);
        }
    }

    [Fact]
    public void PinnedZip_MismatchRefusesBeforeDestinationOrProvenance()
    {
        string temp = NewTempRoot();
        try
        {
            ZipFixture fixture = BuildFixture(temp);
            string state = Path.Combine(temp, "state");
            InternalLocalPayloadRequest badHash = new(
                fixture.ZipPath,
                new string('0', 64),
                fixture.Length,
                state);
            InternalLocalPayloadResult hashResult = InternalLocalPayloadContract.Prepare(badHash);
            Assert.Equal(InternalLocalPayloadState.ZipMismatch, hashResult.State);
            AssertNoPreparedArtifacts(state, fixture.Sha256);

            InternalLocalPayloadRequest badLength = new(
                fixture.ZipPath,
                fixture.Sha256,
                fixture.Length + 1,
                state);
            InternalLocalPayloadResult lengthResult = InternalLocalPayloadContract.Prepare(badLength);
            Assert.Equal(InternalLocalPayloadState.ZipMismatch, lengthResult.State);
            AssertNoPreparedArtifacts(state, fixture.Sha256);
        }
        finally
        {
            TryRemove(temp);
        }
    }

    [Fact]
    public void StrictManifestShape_RefusesUnknownMissingDuplicateAndWrongNoteType()
    {
        AssertPrepared(m =>
        {
            JsonObject setup = m["payload"]!["setupExe"]!.AsObject();
            setup.Remove("note");
        });
        AssertPrepared();

        AssertManifestRefused(m => m["unexpected"] = true);
        AssertManifestRefused(m => m.Remove("product"));
        AssertManifestRefused(
            mutateJson: json => json.Replace(
                "\"product\":\"PAXCookbook\"",
                "\"product\":\"PAXCookbook\",\"product\":\"PAXCookbook\"",
                StringComparison.Ordinal));
        AssertManifestRefused(m => m["payload"]!["setupExe"]!["note"] = 7);
        AssertManifestRefused(m => m["payload"]!["appExe"]!["unexpected"] = "x");
        AssertManifestRefused(m => m["payload"]!.AsObject().Remove("files"));
        AssertManifestRefused(m => m["webView2RuntimeRequirement"]!["detectionPaths"] = "not-an-array");
    }

    [Fact]
    public void ArchiveAndManifestMembership_RefuseEveryUnclosedOrUnsafeShape()
    {
        AssertPrepared();

        AssertState(
            InternalLocalPayloadState.ArchiveRefused,
            extraEntries: new[] { ("rogue.bin", Bytes("rogue")) });
        AssertState(
            InternalLocalPayloadState.PayloadMismatch,
            omittedEntries: new[] { SupportPath });
        AssertState(
            InternalLocalPayloadState.ArchiveRefused,
            extraEntries: new[] { ("setup/paxcookbooksetup.dll", Bytes("duplicate")) });
        AssertState(
            InternalLocalPayloadState.ArchiveRefused,
            extraEntries: new[] { ("../escape.bin", Bytes("escape")) });
        AssertState(
            InternalLocalPayloadState.ArchiveRefused,
            extraEntries: new[] { (@"C:\escape.bin", Bytes("escape")) });
        AssertState(
            InternalLocalPayloadState.ArchiveRefused,
            extraEntries: new[] { ("Setup/./ambiguous.dll", Bytes("ambiguous")) });

        AssertManifestRefused(m =>
        {
            JsonArray files = m["payload"]!["files"]!.AsArray();
            files.Add(new JsonObject
            {
                ["relativeInstallPath"] = @"setup\PAXCookbookSetup.dll",
                ["sha256"] = Sha256Hex(Bytes("support-bytes")),
                ["sizeBytes"] = Bytes("support-bytes").Length
            });
        });
        AssertManifestRefused(m => m["payload"]!["files"]![0]!["relativeInstallPath"] = "../escape.dll");
        AssertManifestRefused(m => m["payload"]!["files"]![0]!["sha256"] = "not-a-hash");
        AssertManifestRefused(m => m["payload"]!["files"]![0]!["sizeBytes"] = -1);
        AssertState(
            InternalLocalPayloadState.PayloadMismatch,
            mutateManifest: m => m["payload"]!["files"]![0]!["sizeBytes"] = 999);
        AssertState(
            InternalLocalPayloadState.PayloadMismatch,
            mutateManifest: m => m["payload"]!["files"]![0]!["sha256"] = new string('A', 64));
    }

    [Fact]
    public void ArchiveDirectories_MustBeNecessaryParentsOfDeclaredFiles()
    {
        AssertPrepared(extraEntries: new[]
        {
            ("App/", Array.Empty<byte>()),
            ("App/bin/", Array.Empty<byte>())
        });
        AssertState(
            InternalLocalPayloadState.ArchiveRefused,
            extraEntries: new[] { ("rogue/", Array.Empty<byte>()) });
    }

    [Fact]
    public void ArgumentVectors_AreExactStructuredAndReadOnly()
    {
        string temp = NewTempRoot();
        try
        {
            ZipFixture fixture = BuildFixture(temp);
            InternalLocalPayloadResult result = Prepare(fixture, Path.Combine(temp, "state"));

            Assert.Equal(
                new[] { "install", "--silent", "--payload-root", result.ValidatedPayloadRoot! },
                result.InstallArguments);
            Assert.Equal(new[] { "uninstall", "--silent" }, result.UninstallArguments);
            Assert.DoesNotContain("/S", result.InstallArguments);
            Assert.DoesNotContain("/S", result.UninstallArguments);
            Assert.DoesNotContain(result.InstallArguments, value => value.Contains("install --silent", StringComparison.Ordinal));
            Assert.Throws<NotSupportedException>(() =>
                ((IList<string>)result.InstallArguments)[0] = "changed");
            Assert.Throws<NotSupportedException>(() =>
                ((IList<string>)result.UninstallArguments)[0] = "changed");

            Assert.Equal(
                new[] { "CycleStateRoot", "ExpectedLength", "ExpectedSha256", "ZipPath" },
                typeof(InternalLocalPayloadRequest).GetProperties().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal));
            Assert.DoesNotContain(
                typeof(InternalLocalPayloadResult).GetProperties(),
                property => property.Name.Contains("Error", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("Message", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("Exception", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryRemove(temp);
        }
    }

    [Fact]
    public void Program_ExplicitPayloadRootStructurallyExcludesDownloader()
    {
        const string positive = """
            if (!string.IsNullOrEmpty(parsed.PayloadRoot))
            {
                resolver = new DirectoryPayloadSourceResolver(parsed.PayloadRoot!);
            }
            else
            {
                var downloader = new PayloadDownloader(log, progress);
            }
            var src = resolver.Resolve();
            """;
        const string negative = """
            if (!string.IsNullOrEmpty(parsed.PayloadRoot))
            {
                resolver = new DirectoryPayloadSourceResolver(parsed.PayloadRoot!);
                var downloader = new PayloadDownloader(log, progress);
            }
            else
            {
                resolver = new EmbeddedPayloadSourceResolver();
            }
            var src = resolver.Resolve();
            """;

        Assert.True(ExplicitPayloadRootExcludesDownloader(positive));
        Assert.False(ExplicitPayloadRootExcludesDownloader(negative));
        Assert.True(ExplicitPayloadRootExcludesDownloader(ReadProgramSource()));
    }

    [Fact]
    public void Program_IsolationInvariantPrecedesLoggerAndDownloader()
    {
        const string positive = """
            if (SetupTestIsolation.IsActive)
            {
                if (SetupTestIsolation.IsMutatingVerb(parsed.Verb))
                {
                    bool wouldNeedNetwork = parsed.Verb is "install" or "update" or "repair" or "apply-update";
                    if (wouldNeedNetwork
                        && !SetupTestIsolation.Context!.NetworkPayloadDownloadEnabled
                        && string.IsNullOrEmpty(parsed.PayloadRoot)
                        && !EmbeddedPayloadSourceResolver.HasEmbeddedPayload())
                    {
                        Console.Error.WriteLine(
                            "test-isolation invariant: network payload download is forbidden; "
                            + "supply --payload-root inside the isolated root");
                        return SetupExitCodes.TestIsolationViolation;
                    }
                }
            }
            var logsDir = Path.Combine(installRoot, "Logs", "Setup");
            var downloader = new PayloadDownloader(log, progress);
            """;
        const string negative = """
            var logsDir = Path.Combine(installRoot, "Logs", "Setup");
            if (SetupTestIsolation.IsActive)
            {
                if (SetupTestIsolation.IsMutatingVerb(parsed.Verb)
                    && !SetupTestIsolation.Context!.NetworkPayloadDownloadEnabled
                    && string.IsNullOrEmpty(parsed.PayloadRoot))
                {
                    return SetupExitCodes.TestIsolationViolation;
                }
            }
            var downloader = new PayloadDownloader(log, progress);
            """;

        Assert.True(IsolationInvariantPrecedesEffects(positive));
        Assert.False(IsolationInvariantPrecedesEffects(negative));
        Assert.True(IsolationInvariantPrecedesEffects(ReadProgramSource()));
    }

    [Fact]
    public void ContractSource_HasOnlyLocalDataAuthority()
    {
        string source = File.ReadAllText(Path.Combine(
            Phase5Fixture.RepoRoot(), "src", "PAXCookbookSetup", "Payload", "InternalLocalPayloadContract.cs"));
        string executable = StripCommentsAndStrings(source);
        string[] prohibited =
        {
            "HttpClient", "PayloadDownloader", "Process", "ProcessStartInfo", "Registry",
            "X509", "ServiceController", "PAX_Purview_Audit_Log_Processor", "Bake",
            "Func<", "Action<", "delegate"
        };

        Assert.DoesNotContain(prohibited, token => executable.Contains(token, StringComparison.Ordinal));
    }

    private static void AssertPrepared(
        Action<JsonObject>? mutateManifest = null,
        IEnumerable<(string Path, byte[] Bytes)>? extraEntries = null)
    {
        string temp = NewTempRoot();
        try
        {
            ZipFixture fixture = BuildFixture(
                temp,
                mutateManifest: mutateManifest,
                extraEntries: extraEntries);
            InternalLocalPayloadResult result = Prepare(fixture, Path.Combine(temp, "state"));
            Assert.True(result.Success);
            Assert.Equal(InternalLocalPayloadState.Prepared, result.State);
        }
        finally
        {
            TryRemove(temp);
        }
    }

    private static void AssertManifestRefused(
        Action<JsonObject>? mutateManifest = null,
        Func<string, string>? mutateJson = null)
    {
        AssertState(
            InternalLocalPayloadState.ManifestRefused,
            mutateManifest: mutateManifest,
            mutateJson: mutateJson);
    }

    private static void AssertState(
        InternalLocalPayloadState expected,
        Action<JsonObject>? mutateManifest = null,
        Func<string, string>? mutateJson = null,
        IEnumerable<(string Path, byte[] Bytes)>? extraEntries = null,
        IEnumerable<string>? omittedEntries = null)
    {
        string temp = NewTempRoot();
        try
        {
            ZipFixture fixture = BuildFixture(
                temp,
                mutateManifest,
                mutateJson,
                extraEntries,
                omittedEntries);
            string state = Path.Combine(temp, "state");
            InternalLocalPayloadResult result = Prepare(fixture, state);
            Assert.False(result.Success);
            Assert.Equal(expected, result.State);
            AssertNoPreparedArtifacts(state, fixture.Sha256);
        }
        finally
        {
            TryRemove(temp);
        }
    }

    private static InternalLocalPayloadResult Prepare(ZipFixture fixture, string stateRoot) =>
        InternalLocalPayloadContract.Prepare(new InternalLocalPayloadRequest(
            fixture.ZipPath,
            fixture.Sha256,
            fixture.Length,
            stateRoot));

    private static ZipFixture BuildFixture(
        string temp,
        Action<JsonObject>? mutateManifest = null,
        Func<string, string>? mutateJson = null,
        IEnumerable<(string Path, byte[] Bytes)>? extraEntries = null,
        IEnumerable<string>? omittedEntries = null)
    {
        byte[] app = Bytes("app-bytes");
        byte[] support = Bytes("support-bytes");
        JsonObject manifest = BuildManifest(app, support);
        mutateManifest?.Invoke(manifest);
        string manifestJson = manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        if (mutateJson is not null)
        {
            manifestJson = mutateJson(manifestJson);
        }

        HashSet<string> omitted = new(
            omittedEntries ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
        var entries = new List<(string Path, byte[] Bytes)>
        {
            ("manifest.json", Bytes(manifestJson)),
            (AppPath, app),
            (SupportPath, support)
        };
        entries.RemoveAll(entry => omitted.Contains(entry.Path));
        if (extraEntries is not null)
        {
            entries.AddRange(extraEntries);
        }

        string zipPath = Path.Combine(temp, "payload.zip");
        using (FileStream stream = new(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach ((string path, byte[] bytes) in entries)
            {
                ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
                using Stream destination = entry.Open();
                destination.Write(bytes);
            }
        }

        return new ZipFixture(zipPath, Sha256Hex(File.ReadAllBytes(zipPath)), new FileInfo(zipPath).Length);
    }

    private static JsonObject BuildManifest(byte[] app, byte[] support) => new()
    {
        ["product"] = "PAXCookbook",
        ["manifestSchemaVersion"] = 1,
        ["appVersion"] = "2.0.0",
        ["setupVersion"] = "2.0.0",
        ["buildId"] = "internal-test",
        ["builtAtUtc"] = "2026-08-31T00:00:00Z",
        ["channel"] = "internal",
        ["targetOs"] = "windows",
        ["targetArch"] = "x64",
        ["webView2RuntimeRequirement"] = new JsonObject
        {
            ["minimumPv"] = "0.0.0",
            ["detectionPaths"] = new JsonArray("HKCU\\Software\\Test")
        },
        ["payload"] = new JsonObject
        {
            ["setupExe"] = new JsonObject
            {
                ["name"] = "PAXCookbookSetup.exe",
                ["sha256"] = new string('B', 64),
                ["sizeBytes"] = 123,
                ["note"] = "Bootstrapper metadata only."
            },
            ["appExe"] = new JsonObject
            {
                ["name"] = "PAX Cookbook.exe",
                ["sha256"] = Sha256Hex(app),
                ["sizeBytes"] = app.Length,
                ["relativeInstallPath"] = @"App\bin\PAX Cookbook.exe"
            },
            ["files"] = new JsonArray
            {
                new JsonObject
                {
                    ["relativeInstallPath"] = @"Setup\PAXCookbookSetup.dll",
                    ["sha256"] = Sha256Hex(support),
                    ["sizeBytes"] = support.Length
                }
            }
        }
    };

    private static bool ExplicitPayloadRootExcludesDownloader(string source)
    {
        const string marker = "if (!string.IsNullOrEmpty(parsed.PayloadRoot))";
        int branchStart = source.IndexOf(marker, StringComparison.Ordinal);
        if (branchStart < 0) return false;
        int blockStart = source.IndexOf('{', branchStart);
        int blockEnd = FindMatchingBrace(source, blockStart);
        if (blockEnd < 0) return false;
        string explicitBranch = source.Substring(blockStart, blockEnd - blockStart + 1);
        if (!explicitBranch.Contains("new DirectoryPayloadSourceResolver", StringComparison.Ordinal)) return false;
        if (explicitBranch.Contains("PayloadDownloader", StringComparison.Ordinal)) return false;
        int commonResolve = source.IndexOf("var src = resolver.Resolve();", blockEnd, StringComparison.Ordinal);
        int downloader = source.IndexOf("new PayloadDownloader", blockEnd, StringComparison.Ordinal);
        if (downloader < 0 || commonResolve < 0 || downloader > commonResolve) return false;
        string exclusiveRegion = source.Substring(blockEnd + 1, downloader - blockEnd - 1);
        return exclusiveRegion.Contains("else", StringComparison.Ordinal);
    }

    private static bool IsolationInvariantPrecedesEffects(string source)
    {
        const string marker = "if (SetupTestIsolation.IsActive)";
        int guardStart = source.IndexOf(marker, StringComparison.Ordinal);
        if (guardStart < 0) return false;
        int logger = source.IndexOf("var logsDir", StringComparison.Ordinal);
        int downloader = source.IndexOf("new PayloadDownloader", StringComparison.Ordinal);
        if (logger <= guardStart || downloader <= logger) return false;
        string guard = source.Substring(guardStart, logger - guardStart);
        string[] required =
        {
            "SetupTestIsolation.IsMutatingVerb(parsed.Verb)",
            "parsed.Verb is \"install\" or \"update\" or \"repair\" or \"apply-update\"",
            "!SetupTestIsolation.Context!.NetworkPayloadDownloadEnabled",
            "string.IsNullOrEmpty(parsed.PayloadRoot)",
            "!EmbeddedPayloadSourceResolver.HasEmbeddedPayload()",
            "test-isolation invariant: network payload download is forbidden; ",
            "supply --payload-root inside the isolated root",
            "return SetupExitCodes.TestIsolationViolation;"
        };
        return required.All(token => guard.Contains(token, StringComparison.Ordinal));
    }

    private static int FindMatchingBrace(string source, int openBrace)
    {
        if (openBrace < 0 || openBrace >= source.Length || source[openBrace] != '{') return -1;
        bool lineComment = false;
        bool blockComment = false;
        bool quoted = false;
        bool verbatim = false;
        char quote = '\0';
        int depth = 0;
        for (int index = openBrace; index < source.Length; index++)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (lineComment)
            {
                if (current == '\n') lineComment = false;
                continue;
            }
            if (blockComment)
            {
                if (current == '*' && next == '/') { blockComment = false; index++; }
                continue;
            }
            if (quoted)
            {
                if (verbatim && current == '"' && next == '"') { index++; continue; }
                if (!verbatim && current == '\\') { index++; continue; }
                if (current == quote) { quoted = false; verbatim = false; }
                continue;
            }
            if (current == '/' && next == '/') { lineComment = true; index++; continue; }
            if (current == '/' && next == '*') { blockComment = true; index++; continue; }
            if (current == '@' && next == '"') { quoted = true; verbatim = true; quote = '"'; index++; continue; }
            if (current == '"' || current == '\'') { quoted = true; quote = current; continue; }
            if (current == '{') depth++;
            if (current == '}' && --depth == 0) return index;
        }
        return -1;
    }

    private static string StripCommentsAndStrings(string source)
    {
        var output = new StringBuilder(source.Length);
        bool lineComment = false;
        bool blockComment = false;
        bool quoted = false;
        bool verbatim = false;
        char quote = '\0';
        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (lineComment)
            {
                if (current == '\n') { lineComment = false; output.Append('\n'); }
                else output.Append(' ');
                continue;
            }
            if (blockComment)
            {
                if (current == '*' && next == '/') { output.Append("  "); blockComment = false; index++; }
                else output.Append(current == '\n' ? '\n' : ' ');
                continue;
            }
            if (quoted)
            {
                if (verbatim && current == '"' && next == '"') { output.Append("  "); index++; continue; }
                if (!verbatim && current == '\\') { output.Append("  "); index++; continue; }
                output.Append(current == '\n' ? '\n' : ' ');
                if (current == quote) { quoted = false; verbatim = false; }
                continue;
            }
            if (current == '/' && next == '/') { output.Append("  "); lineComment = true; index++; continue; }
            if (current == '/' && next == '*') { output.Append("  "); blockComment = true; index++; continue; }
            if (current == '@' && next == '"') { output.Append("  "); quoted = true; verbatim = true; quote = '"'; index++; continue; }
            if (current == '"' || current == '\'') { output.Append(' '); quoted = true; quote = current; continue; }
            output.Append(current);
        }
        return output.ToString();
    }

    private static string ReadProgramSource() => File.ReadAllText(Path.Combine(
        Phase5Fixture.RepoRoot(), "src", "PAXCookbookSetup", "Program.cs"));

    private static void AssertNoPreparedArtifacts(string stateRoot, string sha256)
    {
        Assert.False(Directory.Exists(Path.Combine(stateRoot, "PAXCookbookPayload_" + sha256)));
        Assert.False(File.Exists(Path.Combine(stateRoot, "PAXCookbookPayload_" + sha256 + ".provenance.json")));
    }

    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string NewTempRoot()
    {
        string path = Path.Combine(Path.GetTempPath(), "pax-c144-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryRemove(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record ZipFixture(string ZipPath, string Sha256, long Length);
}