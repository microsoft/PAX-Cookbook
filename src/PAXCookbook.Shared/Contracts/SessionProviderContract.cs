using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PAXCookbook.Shared.Contracts;

// Canonical, cross-component contract for the SELECTED session-authentication
// provider.
//
// PAX Cookbook uses exactly one selected session-authentication provider at a
// time — Windows Hello OR the work account (OAuth / native WAM) — never both,
// with no automatic fallback in either direction. Setup selects the initial
// provider, Settings can switch it atomically from an authenticated session,
// and Setup Repair can repair or explicitly replace a broken selection before
// the app unlocks.
//
// THIS FILE IS THE SINGLE SOURCE OF TRUTH. It is compiled into PAXCookbook.Shared
// (used by the Setup installer) AND linked directly into the native host
// (PAXCookbook.App, which does not reference Shared) so both use the exact same
// path, schema version, enum values, timestamps, and validation with no
// duplicated constants that can drift.
//
// Doctrine (binding):
//   - No secret, token, account handle, tenant, or client identifier is ever
//     written here. The only values are the provider identity, the schema
//     version, the recording source, and a UTC timestamp.
//   - Reads FAIL CLOSED. A missing file MIGRATES to Windows Hello (the historic
//     default). A present-but-malformed / unknown / wrong-schema record does NOT
//     guess a provider; it surfaces a recovery-required signal so the caller
//     drives the operator to Setup repair.
//   - Writes are atomic (temp file, then replace).
//   - The record is preserved across update/repair and is removed only by the
//     product's normal user-data preservation policy on uninstall.
public static class SessionProviderStore
{
    private const string ProductFolder = "PAXCookbook";
    private const string ConfigFolder = "Config";
    private const string SelectionFileName = "session-provider.json";

    public const int CurrentSchemaVersion = 1;

    // The exact, closed provider identities. These strings are the durable and
    // wire contract; keep them stable.
    public const string WindowsHelloId = "windows_hello";
    public const string WorkAccountId = "work_account";

    // The exact, closed recording-source identities.
    public const string SourceSetup = "setup";
    public const string SourceSettings = "settings";
    public const string SourceSetupRepair = "setup_repair";
    public const string SourceMigration = "migration";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    // <localAppDataBase>\PAXCookbook\Config\session-provider.json. The base is
    // the per-user %LOCALAPPDATA% root (or an isolated test/override base). Both
    // Setup and the app pass the same base so they resolve the identical file.
    public static string ResolveSelectionPath(string localAppDataBase)
        => Path.Combine(localAppDataBase, ProductFolder, ConfigFolder, SelectionFileName);

    // Reads the selected provider.
    //   - No file            -> WindowsHello, RecoveryRequired = false (migrate).
    //   - Unreadable / bad
    //     JSON / wrong schema
    //     / unknown provider
    //     / unknown source
    //     / bad timestamp     -> RecoveryRequired = true (fail closed).
    public static SessionProviderSelection Load(string localAppDataBase)
    {
        string path = ResolveSelectionPath(localAppDataBase);
        if (!File.Exists(path))
        {
            return SessionProviderSelection.MigratedDefault();
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch
        {
            return SessionProviderSelection.Recovery();
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return SessionProviderSelection.Recovery();
        }

        SessionProviderFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SessionProviderFileDto>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return SessionProviderSelection.Recovery();
        }

        if (dto is null || dto.SchemaVersion != CurrentSchemaVersion)
        {
            return SessionProviderSelection.Recovery();
        }

        SelectedSessionProvider provider;
        switch (dto.SelectedProvider)
        {
            case WindowsHelloId: provider = SelectedSessionProvider.WindowsHello; break;
            case WorkAccountId: provider = SelectedSessionProvider.WorkAccount; break;
            default: return SessionProviderSelection.Recovery();
        }

        if (!TryParseSource(dto.Source, out ProviderSelectionSource source))
        {
            return SessionProviderSelection.Recovery();
        }

        if (string.IsNullOrWhiteSpace(dto.RecordedUtc) ||
            !DateTimeOffset.TryParse(dto.RecordedUtc, out DateTimeOffset recordedUtc))
        {
            return SessionProviderSelection.Recovery();
        }

        return SessionProviderSelection.Selected(provider, source, recordedUtc);
    }

    // Persists the selected provider atomically. Called only by explicit,
    // verified selection acts (Setup selection, Setup repair, Settings switch).
    // The recording source names which of those wrote the record.
    public static void Save(string localAppDataBase, SelectedSessionProvider provider, ProviderSelectionSource source)
        => Save(localAppDataBase, provider, source, DateTimeOffset.UtcNow);

    // Overload with an explicit timestamp (deterministic tests).
    public static void Save(
        string localAppDataBase,
        SelectedSessionProvider provider,
        ProviderSelectionSource source,
        DateTimeOffset recordedUtc)
    {
        string path = ResolveSelectionPath(localAppDataBase);
        string dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var dto = new SessionProviderFileDto
        {
            SchemaVersion = CurrentSchemaVersion,
            SelectedProvider = ToWire(provider),
            Source = SourceToWire(source),
            RecordedUtc = recordedUtc.ToUniversalTime().ToString("o"),
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

    // Deletes the selection record if present (used only where the product's
    // user-data policy explicitly removes local configuration). No-op when
    // absent; best-effort on a locked/permission failure.
    public static void Delete(string localAppDataBase)
    {
        string path = ResolveSelectionPath(localAppDataBase);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort.
        }
    }

    public static string ToWire(SelectedSessionProvider provider) => provider switch
    {
        SelectedSessionProvider.WorkAccount => WorkAccountId,
        _ => WindowsHelloId,
    };

    public static string SourceToWire(ProviderSelectionSource source) => source switch
    {
        ProviderSelectionSource.Setup => SourceSetup,
        ProviderSelectionSource.Settings => SourceSettings,
        ProviderSelectionSource.SetupRepair => SourceSetupRepair,
        _ => SourceMigration,
    };

    public static bool TryParseSource(string? wire, out ProviderSelectionSource source)
    {
        switch (wire)
        {
            case SourceSetup: source = ProviderSelectionSource.Setup; return true;
            case SourceSettings: source = ProviderSelectionSource.Settings; return true;
            case SourceSetupRepair: source = ProviderSelectionSource.SetupRepair; return true;
            case SourceMigration: source = ProviderSelectionSource.Migration; return true;
            default: source = ProviderSelectionSource.Migration; return false;
        }
    }

    private sealed class SessionProviderFileDto
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("selectedProvider")]
        public string? SelectedProvider { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }

        [JsonPropertyName("recordedUtc")]
        public string? RecordedUtc { get; set; }
    }
}

// The two mutually-exclusive session-authentication providers.
public enum SelectedSessionProvider
{
    WindowsHello,
    WorkAccount,
}

// Which explicit act recorded the current selection.
public enum ProviderSelectionSource
{
    Setup,
    Settings,
    SetupRepair,
    Migration,
}

// Immutable result of reading the selected provider.
//   - RecoveryRequired == true means the persisted selection is missing/invalid
//     in a way that must NOT be silently defaulted; the caller drives the
//     operator to Setup repair. Provider is meaningless in that case.
//   - Migrated == true means there was no file and Windows Hello was applied as
//     the historic default (a normal, non-error condition).
public sealed class SessionProviderSelection
{
    private SessionProviderSelection(
        SelectedSessionProvider provider,
        bool recoveryRequired,
        bool migrated,
        ProviderSelectionSource source,
        DateTimeOffset? recordedUtc)
    {
        Provider = provider;
        RecoveryRequired = recoveryRequired;
        Migrated = migrated;
        Source = source;
        RecordedUtc = recordedUtc;
    }

    public SelectedSessionProvider Provider { get; }

    public bool RecoveryRequired { get; }

    public bool Migrated { get; }

    public ProviderSelectionSource Source { get; }

    public DateTimeOffset? RecordedUtc { get; }

    public static SessionProviderSelection Selected(
        SelectedSessionProvider provider,
        ProviderSelectionSource source,
        DateTimeOffset recordedUtc)
        => new(provider, recoveryRequired: false, migrated: false, source, recordedUtc);

    public static SessionProviderSelection MigratedDefault()
        => new(SelectedSessionProvider.WindowsHello, recoveryRequired: false, migrated: true,
            ProviderSelectionSource.Migration, recordedUtc: null);

    public static SessionProviderSelection Recovery()
        => new(SelectedSessionProvider.WindowsHello, recoveryRequired: true, migrated: false,
            ProviderSelectionSource.Migration, recordedUtc: null);
}
