using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PAXCookbook.App;

// Durable, per-user store for the experimental Entra WAM configuration
// (Track 1 / T1-S3 Phase 2).
//
// Purpose: make the experimental Work-account provider configurable WITHOUT
// developer tooling or process environment variables. A customer administrator
// supplies the three non-secret identifiers (tenant, client, resource App ID
// URI) once — through the Settings experience or the onboarding import — and
// they persist under the per-user install anchor so every subsequent launch
// resolves them with no environment injection.
//
// Doctrine (binding, unchanged by this store):
//   - ONLY non-secret values are ever written here. No client secret,
//     certificate, token, account handle, or granted-scope value is stored.
//   - The provider identifier is exact and closed ("entra-wam").
//   - Nothing here is hardcoded: the file is created only when an operator or
//     the onboarding import supplies real values.
//   - Reads FAIL CLOSED: a missing, empty, unreadable, or malformed file yields
//     "no durable config", never a partially-defaulted configuration.
//
// The store never validates the identifiers itself; it round-trips the raw
// values and lets ExperimentalWamOptions.Create perform the deterministic
// validation, so the parse remains the single validation authority.
internal static class ExperimentalWamConfigStore
{
    private const string ProductFolder = "PAXCookbook";
    private const string ConfigFolder = "Config";
    private const string ConfigFileName = "experimental-wam.json";

    // Current durable schema version. The superseded two-registration model was
    // schema 1; this one-registration model is schema 2. A file whose version is
    // not exactly the current version is treated as malformed (migration-
    // required), never silently reinterpreted.
    internal const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // Resolves the durable config path under the per-user install anchor:
    // <localAppDataBase>\PAXCookbook\Config\experimental-wam.json. The base is
    // resolved by the same anchor rule the engine acquisition state uses, so an
    // isolated test fixture (or the --engine-localappdata override) points both
    // at the same isolated profile.
    internal static string ResolveConfigPath(string localAppDataBase)
        => Path.Combine(localAppDataBase, ProductFolder, ConfigFolder, ConfigFileName);

    // Reads the durable config. Returns null when no durable configuration is
    // present or the file cannot be read/parsed (fail closed). A present-but-
    // malformed file returns a record with Present=true and the raw (possibly
    // blank) values so the status classifier can distinguish "not configured"
    // from "invalid configuration".
    internal static ExperimentalWamStoredConfig? Load(string localAppDataBase)
    {
        string path = ResolveConfigPath(localAppDataBase);
        if (!File.Exists(path))
        {
            return null;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch
        {
            // Unreadable (locked, permissions, IO): fail closed.
            return null;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        ExperimentalWamConfigFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ExperimentalWamConfigFileDto>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            // Present but unparseable JSON: surface as a present-but-invalid
            // config so the classifier reports invalid_configuration rather than
            // silently reverting to not_configured.
            return ExperimentalWamStoredConfig.Malformed();
        }

        if (dto is null)
        {
            return ExperimentalWamStoredConfig.Malformed();
        }

        // Any schema version other than the current one (including the
        // superseded two-registration schema 1) is migration-required, surfaced
        // as a present-but-malformed config so the classifier reports
        // invalid_configuration rather than silently reinterpreting it.
        if (dto.SchemaVersion != CurrentSchemaVersion)
        {
            return ExperimentalWamStoredConfig.Malformed();
        }

        return new ExperimentalWamStoredConfig(
            enabled: dto.Enabled,
            providerId: dto.ProviderId,
            tenantId: dto.TenantId,
            clientId: dto.ClientId,
            malformed: false);
    }

    // Persists the durable config atomically (write to a sibling temp file, then
    // replace). Writes ONLY the non-secret identifiers. Creates the Config
    // directory if needed. Used by the Settings experience and the onboarding
    // import in later phases.
    internal static void Save(string localAppDataBase, ExperimentalWamConfigInput input)
    {
        if (input is null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        string path = ResolveConfigPath(localAppDataBase);
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var dto = new ExperimentalWamConfigFileDto
        {
            SchemaVersion = CurrentSchemaVersion,
            Enabled = input.Enabled,
            ProviderId = input.ProviderId,
            TenantId = input.TenantId,
            ClientId = input.ClientId,
        };

        string json = JsonSerializer.Serialize(dto, SerializerOptions);
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    // Removes the durable config file if present (used by Settings "turn off /
    // remove work-account configuration"). No-op when absent.
    internal static void Delete(string localAppDataBase)
    {
        string path = ResolveConfigPath(localAppDataBase);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort: a locked/permission failure leaves the file in place;
            // the caller surfaces its own bounded failure.
        }
    }

    // On-disk DTO. Only non-secret fields. Property names are the durable
    // contract; keep them stable across versions.
    private sealed class ExperimentalWamConfigFileDto
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("providerId")]
        public string? ProviderId { get; set; }

        [JsonPropertyName("tenantId")]
        public string? TenantId { get; set; }

        [JsonPropertyName("clientId")]
        public string? ClientId { get; set; }
    }
}

// Immutable snapshot of a durable configuration read. Present distinguishes a
// real file from "no durable config"; Malformed marks a present-but-unreadable
// file so the status classifier can report invalid_configuration.
internal sealed class ExperimentalWamStoredConfig
{
    internal ExperimentalWamStoredConfig(
        bool enabled,
        string? providerId,
        string? tenantId,
        string? clientId,
        bool malformed)
    {
        Enabled = enabled;
        ProviderId = providerId;
        TenantId = tenantId;
        ClientId = clientId;
        IsMalformed = malformed;
    }

    internal bool Enabled { get; }

    internal string? ProviderId { get; }

    internal string? TenantId { get; }

    internal string? ClientId { get; }

    // True when the file existed but could not be parsed / carried a non-current
    // schema version. The raw values are unavailable in this case.
    internal bool IsMalformed { get; }

    internal static ExperimentalWamStoredConfig Malformed() =>
        new(enabled: false, providerId: null, tenantId: null, clientId: null, malformed: true);

    // Projects the stored values onto the untyped validation input consumed by
    // ExperimentalWamOptions.Create.
    internal ExperimentalWamConfigInput ToInput() => new()
    {
        Enabled = Enabled,
        ProviderId = ProviderId,
        TenantId = TenantId,
        ClientId = ClientId,
    };
}
