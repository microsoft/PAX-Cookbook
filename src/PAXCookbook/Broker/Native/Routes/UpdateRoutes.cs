using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3i-A -- update endpoints (state / check / download / apply).
//
//   GET  /api/v1/updates/state    -- in-memory snapshot of update state.
//   POST /api/v1/updates/check    -- HEAD-ed manifest fetch + parse +
//                                   version compare. Always 200 with
//                                   {check, state} envelope (the wire
//                                   shape carries the state token, so
//                                   notConfigured / fetchFailed /
//                                   manifestInvalid / upToDate /
//                                   updateAvailable do NOT collapse to
//                                   different HTTP codes).
//   POST /api/v1/updates/download -- 409 no_manifest_snapshot if no
//                                   prior check stored a snapshot;
//                                   otherwise 200 with {download,
//                                   state} envelope.
//   POST /api/v1/updates/apply    -- CONTROLLED 501 updates_apply_deferred
//                                   for Stage 3i-A. See 3i-B for the
//                                   full apply orchestrator port.
public static class UpdateRoutes
{
    public static void Register(
        IEndpointRouteBuilder       app,
        UpdateStateStore            store,
        IUpdateManifestProbe        probe,
        UpdateManifestParser        parser,
        IUpdatePackageDownloader    downloader,
        Func<UpdateConfigContext>   contextFactory)
    {
        app.MapGet("/api/v1/updates/state", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(BuildStateBody(store, probe, downloader, contextFactory()));
        });

        app.MapPost("/api/v1/updates/check", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var cfg = contextFactory();
            var nowUtc = DateTimeOffset.UtcNow.ToString("o");

            var urlOutcome = probe.ValidateUrl(cfg.ManifestUrl);
            if (!urlOutcome.Ok)
            {
                var state = urlOutcome.Error switch
                {
                    "not_configured" => "notConfigured",
                    _                => "manifestUrlInvalid",
                };
                var check = new UpdateCheckResult(
                    State:             state,
                    CheckedAtUtc:      nowUtc,
                    ManifestUrl:       cfg.ManifestUrl,
                    CurrentVersion:    cfg.CurrentCookbookVersion,
                    LatestVersion:     null,
                    BundledPaxChanges: null,
                    Compatibility:     null,
                    Manifest:          null,
                    Error:             urlOutcome.Error,
                    Message:           urlOutcome.Message);
                store.SetCheckResult(check, manifestSnapshot: null);
                await Results.Json(new
                {
                    check,
                    state = BuildStateBody(store, probe, downloader, cfg),
                }).ExecuteAsync(ctx);
                return;
            }

            var fetch = await probe.FetchAsync(cfg.ManifestUrl!, cfg.CurrentCookbookVersion ?? "", ctx.RequestAborted);
            if (!fetch.Ok)
            {
                var checkState = fetch.Error switch
                {
                    "manifest_too_large" => "manifestInvalid",
                    _                    => "fetchFailed",
                };
                var check = new UpdateCheckResult(
                    State:             checkState,
                    CheckedAtUtc:      nowUtc,
                    ManifestUrl:       cfg.ManifestUrl,
                    CurrentVersion:    cfg.CurrentCookbookVersion,
                    LatestVersion:     null,
                    BundledPaxChanges: null,
                    Compatibility:     null,
                    Manifest:          null,
                    Error:             fetch.Error,
                    Message:           fetch.Message);
                store.SetCheckResult(check, manifestSnapshot: null);
                await Results.Json(new
                {
                    check,
                    state = BuildStateBody(store, probe, downloader, cfg),
                }).ExecuteAsync(ctx);
                return;
            }

            var parsed = parser.Parse(fetch.RawText ?? "");
            if (!parsed.Ok)
            {
                var check = new UpdateCheckResult(
                    State:             "manifestInvalid",
                    CheckedAtUtc:      nowUtc,
                    ManifestUrl:       cfg.ManifestUrl,
                    CurrentVersion:    cfg.CurrentCookbookVersion,
                    LatestVersion:     null,
                    BundledPaxChanges: null,
                    Compatibility:     null,
                    Manifest:          null,
                    Error:             parsed.Error,
                    Message:           parsed.Message);
                store.SetCheckResult(check, manifestSnapshot: null);
                await Results.Json(new
                {
                    check,
                    state = BuildStateBody(store, probe, downloader, cfg),
                }).ExecuteAsync(ctx);
                return;
            }

            var compare = UpdateManifestParser.CompareVersion(
                cfg.CurrentCookbookVersion, parsed.LatestCookbookVersion);
            var resultState = compare < 0 ? "updateAvailable" : "upToDate";

            var bundled = new BundledPaxChanges(
                CurrentVersion: cfg.BundledPaxVersion,
                LatestVersion:  parsed.IncludedPaxVersion,
                CurrentSha256:  cfg.BundledPaxSha256,
                LatestSha256:   parsed.IncludedPaxSha256,
                Changes:        !string.Equals(
                                    cfg.BundledPaxSha256,
                                    parsed.IncludedPaxSha256,
                                    StringComparison.OrdinalIgnoreCase));

            var compatibility = new UpdateCompatibilityBlock(
                MinCookbookVersionForPaxScript:    parsed.MinCookbookForPax,
                MinimumCompatibleInstallerVersion: parsed.MinCompatibleInstaller);

            var checkOk = new UpdateCheckResult(
                State:             resultState,
                CheckedAtUtc:      nowUtc,
                ManifestUrl:       cfg.ManifestUrl,
                CurrentVersion:    cfg.CurrentCookbookVersion,
                LatestVersion:     parsed.LatestCookbookVersion,
                BundledPaxChanges: bundled,
                Compatibility:     compatibility,
                Manifest:          parsed.Snapshot,
                Error:             null,
                Message:           null);
            store.SetCheckResult(checkOk, parsed.Snapshot);

            await Results.Json(new
            {
                check = checkOk,
                state = BuildStateBody(store, probe, downloader, cfg),
            }).ExecuteAsync(ctx);
        });

        app.MapPost("/api/v1/updates/download", async (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            var cfg      = contextFactory();
            var snapshot = store.ManifestSnapshot;
            if (snapshot is null)
            {
                ctx.Response.StatusCode  = 409;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error   = "no_manifest_snapshot",
                    message = "Call POST /api/v1/updates/check first to acquire a manifest snapshot.",
                });
                return;
            }

            var download = await downloader.DownloadAsync(snapshot,
                cfg.CurrentCookbookVersion ?? "", ctx.RequestAborted);
            store.SetDownloadResult(download);

            await Results.Json(new
            {
                download,
                state = BuildStateBody(store, probe, downloader, cfg),
            }).ExecuteAsync(ctx);
        });

        app.MapPost("/api/v1/updates/apply", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new
            {
                error   = "not_implemented",
                code    = "updates_apply_deferred",
                stage   = "3i-B",
                message = "Apply orchestration is not yet ported to the native broker. "
                       + "The PowerShell broker remains the production apply path until Stage 3i-B.",
            },
            statusCode: StatusCodes.Status501NotImplemented);
        });
    }

    // Stage 3i-A -- registers ONLY the controlled-501 /updates/apply
    // route. Used by the host when VERSION.json or the workspace is
    // unavailable so the other /updates/* routes cannot be wired,
    // but the apply route still emits its honest "deferred" envelope.
    public static void RegisterApplyOnly(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/updates/apply", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new
            {
                error   = "not_implemented",
                code    = "updates_apply_deferred",
                stage   = "3i-B",
                message = "Apply orchestration is not yet ported to the native broker. "
                       + "The PowerShell broker remains the production apply path until Stage 3i-B.",
            },
            statusCode: StatusCodes.Status501NotImplemented);
        });
    }

    private static UpdateStateSnapshot BuildStateBody(
        UpdateStateStore         store,
        IUpdateManifestProbe     probe,
        IUpdatePackageDownloader downloader,
        UpdateConfigContext      cfg)
    {
        var urlVal = probe.ValidateUrl(cfg.ManifestUrl);
        var trust = new TrustReadinessBlock(
            AllowlistPresent: false,
            AllowlistError:   null,
            SignerCount:      0);
        return new UpdateStateSnapshot(
            ManifestUrlConfigured:   urlVal.Ok,
            ManifestUrlError:        urlVal.Ok ? null : urlVal.Error,
            CurrentCookbookVersion:  cfg.CurrentCookbookVersion,
            CurrentReleaseChannel:   cfg.CurrentReleaseChannel,
            LastCheck:               store.LastCheck,
            LastDownload:            store.LastDownload,
            StagedPackages:          downloader.GetStagedInventory(),
            TrustReadiness:          trust);
    }
}

// Per-request configuration context. Factoried so the host can refresh
// the cookbook version / manifest URL across hot-reloads of
// VERSION.json without rebuilding the route registration.
public sealed record UpdateConfigContext(
    string? ManifestUrl,
    string? CurrentCookbookVersion,
    string? CurrentReleaseChannel,
    string? BundledPaxVersion,
    string? BundledPaxSha256);
