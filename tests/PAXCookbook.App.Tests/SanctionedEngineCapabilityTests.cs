using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PAXCookbook.App;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle 15 — SANCTIONED ENGINE SHA-256 SELECTOR CAPABILITY.
//
// Prepares PAX Cookbook to consume a FUTURE sanctioned PAX engine that supports
// a fail-closed SHA-256-over-DER certificate selector. The CURRENT engine
// declares no capability, so organization Cook stays BLOCKED. Every capable
// engine in this suite is SYNTHETIC: an in-memory manifest document, an
// install-state document, and a throwaway file under the OS temp directory. No
// real acquisition, no network, no manifest fetch, no certificate, no private
// key, no PAX, no Bake, no process.
public sealed class SanctionedEngineCapabilityTests
{
    private const string Token = "organization_certificate_sha256_selector_v1";

    private const string ShaA = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string ShaB = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string ShaC = "3333333333333333333333333333333333333333333333333333333333333333";

    // =========================================================================
    // TEST-FIRST anchor (matrix 21). An organization-bound Recipe that is FULLY
    // READY by policy / inventory / entry / resolution / usability STILL refuses
    // when the acquired engine does not declare the SHA-256 selector capability.
    // =========================================================================
    [Fact]
    public void TF00_FullyReadyOrganizationRecipe_RemainsBlocked_WhenEngineLacksCapability()
    {
        OrganizationInventoryEvaluation evaluation = OrganizationKeyNotYetRunnableTests.Provisioned(
            OrganizationKeyNotYetRunnableTests.Doc(
                2,
                OrganizationKeyNotYetRunnableTests.V2Entry(
                    OrganizationKeyNotYetRunnableTests.OrgKeyId,
                    OrganizationKeyNotYetRunnableTests.FingerprintA)));

        ICertificateCatalog catalog = OrganizationKeyNotYetRunnableTests.CatalogWith(
            (OrganizationKeyNotYetRunnableTests.FingerprintA,
             OrganizationKeyNotYetRunnableTests.UsableFacts()));

        (int status, object? body, PaxAdapter.ChefKeyAuthRow? row) =
            RecipeReadModel.TestSeamResolveOrganizationCookPreparation(
                OrganizationKeyNotYetRunnableTests.OrgKeyId,
                EngineCapabilityState.NotDeclared,
                evaluation,
                catalog,
                OrganizationKeyNotYetRunnableTests.Clock());

        Assert.Equal(412, status);
        Assert.Null(row);

        string json = JsonSerializer.Serialize(body);
        Assert.Contains("organization_key_not_yet_runnable", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientCertificateSha256", json, StringComparison.Ordinal);
    }

    // =========================================================================
    // A. Manifest schema (matrix 1-8).
    // =========================================================================

    private static string Manifest(int schemaVersion, params string[] entries)
        => "{ \"schemaVersion\": " + schemaVersion +
           ", \"manifestVersion\": 7, \"channel\": \"stable\"," +
           " \"generatedAtUtc\": \"2026-06-01T00:00:00Z\", \"signingKeyId\": \"key-1\"," +
           " \"scripts\": [" + string.Join(",", entries) + "] }";

    private static string Entry(
        string version, string sha, string? capabilitiesJson = null,
        string status = "approved", string min = "1.0.0", string max = "99.0.0")
    {
        string body =
            "{ \"name\": \"PAX\", \"version\": \"" + version + "\", \"sha256\": \"" + sha + "\"," +
            " \"downloadUrl\": \"https://example.invalid/pax.ps1\", \"status\": \"" + status + "\"," +
            " \"minCookbookVersion\": \"" + min + "\", \"maxCookbookVersion\": \"" + max + "\"";
        if (capabilitiesJson is not null)
        {
            body += ", \"capabilities\": " + capabilitiesJson;
        }
        return body + " }";
    }

    private static ManifestValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ManifestSchemaValidator.Validate(doc.RootElement);
    }

    [Fact] // matrix 1 + 2 — schema v1 still validates, and always means "no capability".
    public void M01_SchemaV1_IsStillValid_AndCarriesNoCapability()
    {
        ManifestValidationResult result = Validate(Manifest(1, Entry("1.11.14", ShaA)));

        Assert.True(result.Ok, result.Message);
        Assert.Equal("1", result.SchemaVersion);
        ApprovedEngineEntry entry = Assert.Single(result.Entries);
        Assert.Empty(entry.Capabilities);
    }

    [Fact] // matrix 2 — a v1 entry carrying capabilities is rejected by the CLOSED v1 allow-list.
    public void M02_SchemaV1_EntryWithCapabilities_IsRejectedAsUnknownField()
    {
        ManifestValidationResult result = Validate(
            Manifest(1, Entry("1.11.14", ShaA, "[\"" + Token + "\"]")));

        Assert.False(result.Ok);
        Assert.Equal("unknown_field", result.Error);
    }

    [Fact] // matrix 3 — schema v2 REQUIRES the capabilities array on every entry.
    public void M03_SchemaV2_RequiresCapabilitiesArray()
    {
        ManifestValidationResult result = Validate(Manifest(2, Entry("1.11.15", ShaA)));

        Assert.False(result.Ok);
        Assert.Equal("missing_field", result.Error);
    }

    [Fact] // matrix 4 — an EMPTY array is valid and means "no capabilities".
    public void M04_SchemaV2_EmptyCapabilitiesArray_IsValid()
    {
        ManifestValidationResult result = Validate(Manifest(2, Entry("1.11.15", ShaA, "[]")));

        Assert.True(result.Ok, result.Message);
        Assert.Equal("2", result.SchemaVersion);
        Assert.Empty(Assert.Single(result.Entries).Capabilities);
    }

    [Fact] // matrix 5 — the one recognized token is accepted.
    public void M05_SchemaV2_KnownCapabilityToken_IsValid()
    {
        ManifestValidationResult result = Validate(
            Manifest(2, Entry("1.11.15", ShaA, "[\"" + Token + "\"]")));

        Assert.True(result.Ok, result.Message);
        Assert.Equal(new[] { Token }, Assert.Single(result.Entries).Capabilities);
    }

    [Fact] // matrix 6 — an unrecognized token is a hard reject.
    public void M06_SchemaV2_UnknownCapabilityToken_IsRejected()
    {
        ManifestValidationResult result = Validate(
            Manifest(2, Entry("1.11.15", ShaA, "[\"organization_certificate_sha1_selector_v9\"]")));

        Assert.False(result.Ok);
        Assert.Equal("unknown_capability", result.Error);
    }

    [Fact] // matrix 7 — a duplicate token is a hard reject.
    public void M07_SchemaV2_DuplicateCapabilityToken_IsRejected()
    {
        ManifestValidationResult result = Validate(
            Manifest(2, Entry("1.11.15", ShaA, "[\"" + Token + "\",\"" + Token + "\"]")));

        Assert.False(result.Ok);
        Assert.Equal("duplicate_capability", result.Error);
    }

    [Theory] // matrix 8 — a wrong-typed capabilities value or element is a hard reject.
    [InlineData("\"organization_certificate_sha256_selector_v1\"")]
    [InlineData("{ \"a\": 1 }")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[\"\"]")]
    public void M08_SchemaV2_WrongTypedCapabilities_IsRejected(string capabilitiesJson)
    {
        ManifestValidationResult result = Validate(
            Manifest(2, Entry("1.11.15", ShaA, capabilitiesJson)));

        Assert.False(result.Ok);
        Assert.Equal("type_mismatch", result.Error);
    }

    // =========================================================================
    // B. Capability-required selection (matrix 9-11).
    // =========================================================================

    [Fact] // matrix 9 — capability-required selection excludes every schema-v1 entry.
    public void S01_CapabilityRequiredSelection_ExcludesSchemaV1Entries()
    {
        ManifestValidationResult v1 = Validate(Manifest(1, Entry("1.11.14", ShaA)));
        Assert.True(v1.Ok, v1.Message);

        // Without a required capability the v1 entry is still selected: the
        // optional parameter is backward compatible.
        (ApprovedEngineEntry? plain, _, _, _) =
            ManifestSelector.Select(v1.Entries, "2.0.0", null, null);
        Assert.NotNull(plain);

        (ApprovedEngineEntry? capable, string? error, _, _) =
            ManifestSelector.Select(v1.Entries, "2.0.0", null, null, Token);
        Assert.Null(capable);
        Assert.Equal("no_compatible_engine", error);
    }

    [Fact] // matrix 10 — v2 entries that do not declare the token are excluded.
    public void S02_CapabilityRequiredSelection_ExcludesEntriesWithoutTheToken()
    {
        ManifestValidationResult v2 = Validate(Manifest(2, Entry("1.11.15", ShaA, "[]")));
        Assert.True(v2.Ok, v2.Message);

        (ApprovedEngineEntry? capable, string? error, _, _) =
            ManifestSelector.Select(v2.Entries, "2.0.0", null, null, Token);
        Assert.Null(capable);
        Assert.Equal("no_compatible_engine", error);
    }

    [Fact] // matrix 11 — highest approved + compatible + capable entry wins.
    public void S03_CapabilityRequiredSelection_PicksHighestApprovedCompatibleCapableEntry()
    {
        ManifestValidationResult v2 = Validate(Manifest(
            2,
            Entry("1.11.15", ShaA, "[\"" + Token + "\"]"),
            Entry("1.11.16", ShaB, "[\"" + Token + "\"]"),
            Entry("1.11.17", ShaC, "[]"),
            Entry("1.11.18", ShaA, "[\"" + Token + "\"]", status: "withdrawn"),
            Entry("1.11.19", ShaB, "[\"" + Token + "\"]", min: "9.0.0", max: "9.9.9")));
        Assert.True(v2.Ok, v2.Message);

        (ApprovedEngineEntry? capable, _, _, _) =
            ManifestSelector.Select(v2.Entries, "2.0.0", null, null, Token);

        Assert.NotNull(capable);
        Assert.Equal("1.11.16", capable!.Version);
    }

    // =========================================================================
    // C. Hash-bound persistence (matrix 12-13).
    // =========================================================================

    private static string NewTempBase()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "paxcookbook-cycle15-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static VersionInfo ExternalVersion() => new(
        CookbookVersion: "2.0.0",
        ReleaseChannel: "stable",
        PaxVersion: "1.11.15",
        PaxSha256: new string('0', 64),
        PaxRelativePath: "Engine/pax.ps1",
        PaxAcquisitionPolicy: "external",
        EngineManifestUrl: null,
        EngineManifestTrustAnchorThumbprint: null,
        ManifestSignaturePolicy: "none",
        BuildTimestamp: null);

    private static VersionInfo EmbeddedVersion() => ExternalVersion() with
    {
        PaxAcquisitionPolicy = "embedded",
    };

    // Builds a SYNTHETIC acquired engine: a throwaway file under the OS temp
    // directory activated through the REAL ScriptActivator, so the capability
    // record is written by the same byte-verified path production uses.
    private static (string Root, ActivationResult Result) ActivateSynthetic(
        IReadOnlyList<string>? capabilities,
        string version = "1.11.15",
        string? expectedShaOverride = null)
    {
        string root = NewTempBase();
        string staged = Path.Combine(root, "staged.ps1");
        File.WriteAllText(staged, "# synthetic engine body for capability tests\n");

        ActivationResult result = ScriptActivator.Activate(new ActivationRequest
        {
            StagedFilePath = staged,
            ExpectedSha256 = expectedShaOverride ?? Sha256Hex.OfFile(staged),
            Version = version,
            CanonicalScriptPath = EngineAcquisition.GetManagedEnginePath(root),
            Source = AcquisitionSources.Download,
            Capabilities = capabilities,
            StatePath = EngineAcquisition.GetInstallStatePath(root),
        });
        return (root, result);
    }

    private static JsonObject ReadAcquisitionBlock(string root)
    {
        JsonNode node = JsonNode.Parse(
            File.ReadAllText(EngineAcquisition.GetInstallStatePath(root)))!;
        return (JsonObject)node["paxAcquisition"]!;
    }

    private static void MutateAcquisitionBlock(string root, Action<JsonObject> mutate)
    {
        string statePath = EngineAcquisition.GetInstallStatePath(root);
        JsonNode node = JsonNode.Parse(File.ReadAllText(statePath))!;
        mutate((JsonObject)node["paxAcquisition"]!);
        File.WriteAllText(statePath, node.ToJsonString());
    }

    [Fact] // matrix 12 — the capability is persisted ONLY after the bytes verify.
    public void P01_Capability_IsPersistedOnlyAfterByteVerification()
    {
        // A staged file whose bytes do not match the approved hash never reaches
        // the install-state writer at all: no state, no canonical engine.
        (string badRoot, ActivationResult bad) = ActivateSynthetic(
            new[] { Token }, expectedShaOverride: ShaA);

        Assert.False(bad.Ok);
        Assert.False(File.Exists(EngineAcquisition.GetInstallStatePath(badRoot)));
        Assert.False(File.Exists(EngineAcquisition.GetManagedEnginePath(badRoot)));

        // The verified path records the capability alongside the verified bytes.
        (string goodRoot, ActivationResult good) = ActivateSynthetic(new[] { Token });

        Assert.True(good.Ok, good.Message);
        JsonObject block = ReadAcquisitionBlock(goodRoot);
        Assert.Equal(
            Sha256Hex.OfFile(EngineAcquisition.GetManagedEnginePath(goodRoot)),
            block["sha256"]!.GetValue<string>());
        Assert.Equal(
            new[] { Token },
            block["capabilities"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact] // matrix 12 — a re-acquisition can never leave a STALE capability array.
    public void P02_ReAcquisitionWithoutCapabilities_ClearsThePreviousArray()
    {
        (string root, ActivationResult first) = ActivateSynthetic(new[] { Token });
        Assert.True(first.Ok, first.Message);

        string staged = Path.Combine(root, "staged-2.ps1");
        File.WriteAllText(staged, "# a different synthetic engine body\n");
        ActivationResult second = ScriptActivator.Activate(new ActivationRequest
        {
            StagedFilePath = staged,
            ExpectedSha256 = Sha256Hex.OfFile(staged),
            Version = "1.11.16",
            CanonicalScriptPath = EngineAcquisition.GetManagedEnginePath(root),
            Source = AcquisitionSources.Download,
            Capabilities = null,
            StatePath = EngineAcquisition.GetInstallStatePath(root),
        });

        Assert.True(second.Ok, second.Message);
        JsonObject block = ReadAcquisitionBlock(root);
        Assert.True(block.ContainsKey("capabilities"));
        Assert.Null(block["capabilities"]);
    }

    [Fact] // matrix 13 — a FAILED acquisition writes no capability at all.
    public void P03_FailedAcquisition_WritesNoCapability()
    {
        string root = NewTempBase();
        InstallStateWriter.WriteFailure(
            EngineAcquisition.GetInstallStatePath(root),
            new FailureDetails
            {
                Error = "script_fetch_failed",
                Endpoint = "POST /api/v1/setup/acquire-pax/download",
                Message = "synthetic failure",
            });

        JsonObject block = ReadAcquisitionBlock(root);
        Assert.False(block.ContainsKey("capabilities"));
        Assert.True(block["pending"]!.GetValue<bool>());
    }

    // =========================================================================
    // D. Runtime capability check (matrix 14-20).
    // =========================================================================

    private static EngineCapabilityState Evaluate(string root, VersionInfo? version = null)
        => EngineCapabilityRuntime.Evaluate(version ?? ExternalVersion(), root, Token);

    [Fact] // matrix 14 — a legacy install-state (no capabilities key) is not capable.
    public void R01_LegacyInstallState_IsNotDeclared()
    {
        (string root, ActivationResult act) = ActivateSynthetic(null);
        Assert.True(act.Ok, act.Message);

        // Written as an explicit null by this cycle's writer.
        Assert.Equal(EngineCapabilityState.NotDeclared, Evaluate(root));

        // And as a genuinely absent key, exactly like a pre-cycle-15 record.
        MutateAcquisitionBlock(root, pax => pax.Remove("capabilities"));
        Assert.Equal(EngineCapabilityState.NotDeclared, Evaluate(root));
    }

    [Theory] // matrix 15 — a malformed capability record fails CLOSED as invalid.
    [InlineData("\"organization_certificate_sha256_selector_v1\"")]
    [InlineData("[1]")]
    [InlineData("[\"\"]")]
    [InlineData("[\"not_a_recognized_capability\"]")]
    [InlineData("[\"organization_certificate_sha256_selector_v1\",\"organization_certificate_sha256_selector_v1\"]")]
    public void R02_MalformedCapabilityRecord_IsStateInvalid(string capabilitiesJson)
    {
        (string root, ActivationResult act) = ActivateSynthetic(new[] { Token });
        Assert.True(act.Ok, act.Message);

        MutateAcquisitionBlock(root, pax => pax["capabilities"] = JsonNode.Parse(capabilitiesJson));

        Assert.Equal(EngineCapabilityState.StateInvalid, Evaluate(root));
    }

    [Fact] // matrix 16 — tampered engine bytes are surfaced as a hash mismatch.
    public void R03_EngineHashMismatch_IsNotCapable()
    {
        (string root, ActivationResult act) = ActivateSynthetic(new[] { Token });
        Assert.True(act.Ok, act.Message);

        File.AppendAllText(EngineAcquisition.GetManagedEnginePath(root), "# tampered\n");

        Assert.Equal(EngineCapabilityState.EngineHashMismatch, Evaluate(root));
    }

    [Fact] // matrix 17 — the capability record must be bound to the recorded version.
    public void R04_EngineVersionMismatch_IsNotCapable()
    {
        (string root, ActivationResult act) = ActivateSynthetic(new[] { Token });
        Assert.True(act.Ok, act.Message);

        // A record with no bound version can never be trusted.
        MutateAcquisitionBlock(root, pax => pax.Remove("version"));
        Assert.Equal(EngineCapabilityState.EngineVersionMismatch, Evaluate(root));

        // Nor can one whose bound version disagrees with the acquisition the
        // capability is being claimed for.
        MutateAcquisitionBlock(root, pax => pax["version"] = "1.11.15");
        var divergent = new EngineAcquisitionResult
        {
            Policy = "external",
            State = "acquired",
            IsAcquired = true,
            AcquisitionRequired = false,
            ManagedEnginePathPresent = true,
            Version = "1.11.99",
            Message = "synthetic divergent acquisition",
            InstallStatePath = EngineAcquisition.GetInstallStatePath(root),
            ManagedEnginePath = EngineAcquisition.GetManagedEnginePath(root),
        };
        Assert.Equal(
            EngineCapabilityState.EngineVersionMismatch,
            EngineCapabilityRuntime.Evaluate(divergent, Token));
    }

    [Fact] // matrix 18 — a matching state over matching bytes is capable.
    public void R05_MatchingStateAndBytes_IsAvailable()
    {
        (string root, ActivationResult act) = ActivateSynthetic(new[] { Token });
        Assert.True(act.Ok, act.Message);

        Assert.Equal(EngineCapabilityState.Available, Evaluate(root));
        Assert.Equal("available", EngineCapabilityRuntime.WireToken(EngineCapabilityState.Available));
    }

    [Fact] // matrix 19 — the runtime check performs no network call and no write.
    public void R06_RuntimeCheck_PerformsNoNetworkCallAndNoWrite()
    {
        string src = ReadSource("src/PAXCookbook.App/EngineCapabilityRuntime.cs");

        foreach (string forbidden in new[]
        {
            "HttpClient", "WebClient", "WebRequest", "Socket", "Download",
            "File.Write", "File.Delete", "File.Move", "File.Copy", "File.Append",
            "Directory.Create", "Directory.Delete", "Registry", "SetAccessControl",
            "Process.Start", "new Process(", "ProcessStartInfo",
            "ManifestSelector", "ScriptFetcher", "InstallStateWriter",
        })
        {
            Assert.DoesNotContain(forbidden, src, StringComparison.Ordinal);
        }

        // The only file access it performs is a read-only open.
        Assert.Contains("FileAccess.Read", src, StringComparison.Ordinal);
    }

    [Fact] // matrix 20 — the CURRENT engine stays incapable, so Cook stays blocked.
    public void R07_CurrentEngine_RemainsIncapable()
    {
        // Bundled / embedded policy is not the acquired engine store, so even a
        // synthetic capable record on disk cannot make it capable.
        (string root, ActivationResult act) = ActivateSynthetic(new[] { Token });
        Assert.True(act.Ok, act.Message);
        Assert.Equal(EngineCapabilityState.EngineNotAcquired, Evaluate(root, EmbeddedVersion()));

        // No acquisition at all is not capable.
        Assert.Equal(EngineCapabilityState.EngineNotAcquired, Evaluate(NewTempBase()));

        // An unrecognized capability token can never be "available".
        Assert.Equal(
            EngineCapabilityState.Unknown,
            EngineCapabilityRuntime.Evaluate(ExternalVersion(), root, "some_other_capability"));
        Assert.Equal(EngineCapabilityState.Unknown, EngineCapabilityRuntime.Evaluate(null, Token));

        // The bundled auto-acquire path never declares a capability.
        string bundled = ReadSource("src/PAXCookbook.App/EngineBundleAutoAcquire.cs");
        Assert.DoesNotContain("Capabilities", bundled, StringComparison.Ordinal);
    }

    // ---- helpers -------------------------------------------------------------

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        string dir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }

    private static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));
}

