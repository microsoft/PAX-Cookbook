using System.Text.Json;
using PAXCookbook.Shared.Json;

namespace PAXCookbook.Shared.Contracts;

// Schema-strict mirror of manifest.schema.json.
public sealed record Manifest
{
    public string Product { get; init; } = "PAXCookbook";
    public int ManifestSchemaVersion { get; init; } = 1;
    public string AppVersion { get; init; } = "0.0.0";
    public string SetupVersion { get; init; } = "0.0.0";
    public string BuildId { get; init; } = "";
    public string BuiltAtUtc { get; init; } = "1970-01-01T00:00:00Z";
    public string Channel { get; init; } = "dev";    // dev|beta|stable
    public string TargetOs { get; init; } = "windows";
    public string TargetArch { get; init; } = "x64";
    public int? MinWindowsBuild { get; init; }
    public WebView2RuntimeRequirement WebView2RuntimeRequirement { get; init; } = new();
    public ManifestPayload Payload { get; init; } = new();
    public ManifestSignature? Signature { get; init; }
}

public sealed record WebView2RuntimeRequirement
{
    public string MinimumPv { get; init; } = "0.0.0";
    public List<string> DetectionPaths { get; init; } = new()
    {
        @"HKLM64\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}\pv",
        @"HKLM32\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}\pv",
        @"HKCU\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}\pv"
    };
}

public sealed record ManifestPayload
{
    public ManifestSetupExe SetupExe { get; init; } = new();
    public ManifestAppExe AppExe { get; init; } = new();
    public List<ManifestFile> Files { get; init; } = new();
}

public sealed record ManifestSetupExe
{
    public string Name { get; init; } = "PAXCookbookSetup.exe";
    public string Sha256 { get; init; } = "";
    public long SizeBytes { get; init; }
}

public sealed record ManifestAppExe
{
    public string Name { get; init; } = "PAXCookbook.exe";
    public string Sha256 { get; init; } = "";
    public long SizeBytes { get; init; }
    public string RelativeInstallPath { get; init; } = @"App\bin\PAXCookbook.exe";
}

public sealed record ManifestFile
{
    public string RelativeInstallPath { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long SizeBytes { get; init; }
}

public sealed record ManifestSignature
{
    public string Algorithm { get; init; } = "none"; // authenticode|detached-ed25519|none
    public string Value { get; init; } = "";
}

public static class ManifestSerializer
{
    public static string Serialize(Manifest m)
        => JsonSerializer.Serialize(m, JsonOptionsFactory.Default);

    public static Manifest Deserialize(string json)
    {
        var r = JsonSerializer.Deserialize<Manifest>(json, JsonOptionsFactory.Default);
        if (r is null) throw new InvalidDataException("manifest JSON deserialized to null");
        return r;
    }
}
