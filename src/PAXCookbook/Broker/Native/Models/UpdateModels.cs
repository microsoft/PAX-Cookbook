using System.Collections.Generic;

namespace PAXCookbook.Broker.Native.Models;

// Stage 3i-A -- update wire models.
//
// The wire shape mirrors Get-UpdateStateSnapshot / Invoke-UpdatesCheck
// / Invoke-UpdatesDownload in app/broker/Routes/Updates.ps1 and the
// staged-package inventory emitted by Get-StagedPackageInventory in
// app/broker/Update/Package.psm1.
//
// Stage 3i-A scope (per Brian's split):
//   * /updates/state    -- full port (read-only).
//   * /updates/check    -- full port (HttpClient + parser + version
//                          compare). Bundled PAX delta and
//                          compatibility block surfaced verbatim
//                          from VERSION.json / manifest.
//   * /updates/download -- full port (HttpClient + SHA-256 + atomic
//                          rename + sidecar metadata).
//   * /updates/apply    -- CONTROLLED 501 envelope. The PS apply
//                          orchestrator depends on
//                          Get-ActiveCookSnapshot, broker-lifecycle
//                          shutdown choreography, launcher handoff
//                          JSON, and Add-UpdateRequestObservation
//                          (SQLite append-only) -- none of which
//                          are present in the native broker today.
//                          The route emits HTTP 501
//                          { error="not_implemented",
//                            code="updates_apply_deferred",
//                            stage="3i-B" }
//                          so the SPA can render "apply not available
//                          on native broker" without being tricked
//                          into believing the apply succeeded.
public sealed record UpdateStateSnapshot(
    bool                          ManifestUrlConfigured,
    string?                       ManifestUrlError,
    string?                       CurrentCookbookVersion,
    string?                       CurrentReleaseChannel,
    UpdateCheckResult?            LastCheck,
    UpdateDownloadResult?         LastDownload,
    IReadOnlyList<StagedPackageInfo> StagedPackages,
    TrustReadinessBlock           TrustReadiness);

public sealed record TrustReadinessBlock(
    bool    AllowlistPresent,
    string? AllowlistError,
    int     SignerCount);

public sealed record UpdateCheckResult(
    string?                  State,
    string                   CheckedAtUtc,
    string?                  ManifestUrl,
    string?                  CurrentVersion,
    string?                  LatestVersion,
    BundledPaxChanges?       BundledPaxChanges,
    UpdateCompatibilityBlock? Compatibility,
    object?                  Manifest,
    string?                  Error,
    string?                  Message);

public sealed record BundledPaxChanges(
    string? CurrentVersion,
    string? LatestVersion,
    string? CurrentSha256,
    string? LatestSha256,
    bool    Changes);

public sealed record UpdateCompatibilityBlock(
    string? MinCookbookVersionForPaxScript,
    string? MinimumCompatibleInstallerVersion);

public sealed record UpdateDownloadResult(
    string?  State,
    string   StartedAtUtc,
    string?  FinishedAtUtc,
    string?  Filename,
    string?  Path,
    string?  Sha256,
    long?    SizeBytes,
    bool     AlreadyStaged,
    string?  Error,
    string?  Message);

public sealed record StagedPackageInfo(
    string                    Filename,
    string                    Path,
    long                      SizeBytes,
    string                    ModifiedUtc,
    string?                   MetadataPath,
    StagedPackageMetadata?    Metadata,
    object?                   Trust);

public sealed record StagedPackageMetadata(
    string?  CookbookVersionAtDownload,
    string?  DownloadedAtUtc,
    string?  SourceUrl,
    string?  Filename,
    long?    SizeBytes,
    string?  Sha256,
    string?  ExpectedCookbookVersion,
    object?  ManifestSnapshot);

// Internal probe result -- the surface above is what the route
// serialises; this is the structured fetch outcome the parser
// consumes.
public sealed record ManifestFetchOutcome(
    bool    Ok,
    string? Error,
    string? Message,
    string? RawText,
    string? SourceUrl);

public sealed record ManifestSchemaOutcome(
    bool    Ok,
    string? Error,
    string? Message);
