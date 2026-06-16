namespace PAXCookbook.Broker.Native.Models;

// Stage 3c -- in-memory projection of app\VERSION.json. Only the
// fields needed by GET /api/v1/runtime/version are surfaced. Loaded
// once at broker startup by VersionInfoReader.Load(); missing /
// malformed VERSION.json is reported as IsAvailable=false so the
// runtime route can emit a controlled 500 instead of hiding the
// integrity failure.
public sealed record VersionInfo(
    bool IsAvailable,
    string? CookbookVersion,
    string? ReleaseChannel,
    BundledPaxInfo? BundledPax,
    string? UpdateManifestUrl,
    string? LoadError);

public sealed record BundledPaxInfo(
    string Name,
    string Version,
    string RelativePath,
    string Sha256);
