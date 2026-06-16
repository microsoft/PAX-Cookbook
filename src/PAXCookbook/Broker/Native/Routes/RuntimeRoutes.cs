using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3c -- GET /api/v1/runtime/version. Native subset of the
// PowerShell broker's Invoke-RuntimeVersionGet
// (Routes/Runtime.ps1). The full PS envelope has 9 top-level sections
// (cookbookVersion, releaseChannel, bundledPax, manifest, host, paths,
// runtime, brokerSession, updateReadiness) populated from broker-
// lifecycle state that the native broker has not absorbed yet. The
// native broker surfaces the deterministic subset: VERSION.json
// fields, the native broker's runtime block, and a paths block.
// Sections that depend on future stages (brokerSession evidence,
// manifest alignment beyond the bundled file, updateReadiness check)
// are surfaced as documented placeholders so a side-by-side diff
// against the PS broker remains obvious.
public static class RuntimeRoutes
{
    public static void Register(
        IEndpointRouteBuilder app,
        VersionInfo versionInfo,
        WorkspacePaths? workspacePaths,
        int port,
        DateTimeOffset brokerStartedAtUtc,
        string appRoot,
        string paxScriptAbsolutePath,
        string versionFileAbsolutePath)
    {
        app.MapGet("/api/v1/runtime/version", () =>
        {
            if (!versionInfo.IsAvailable)
            {
                return Results.Json(new
                {
                    error = "version_info_unavailable",
                    detail = versionInfo.LoadError ?? "unknown",
                },
                statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Json(new
            {
                cookbookVersion = versionInfo.CookbookVersion,
                releaseChannel = versionInfo.ReleaseChannel,
                bundledPax = versionInfo.BundledPax is null
                    ? null
                    : new
                    {
                        name         = versionInfo.BundledPax.Name,
                        version      = versionInfo.BundledPax.Version,
                        relativePath = versionInfo.BundledPax.RelativePath,
                        sha256       = versionInfo.BundledPax.Sha256,
                    },
                // manifest{} -- placeholder. PS broker's full manifest
                // alignment block depends on a deferred update-manifest
                // probe (Stage 3d+). The native broker surfaces only
                // the configured URL.
                manifest = new
                {
                    updateManifestUrl           = versionInfo.UpdateManifestUrl,
                    updateManifestUrlConfigured = !string.IsNullOrWhiteSpace(versionInfo.UpdateManifestUrl),
                },
                host = new
                {
                    machineName = Environment.MachineName,
                    osPlatform  = "Windows",
                    osVersion   = Environment.OSVersion.VersionString,
                    framework   = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                },
                paths = new
                {
                    appRoot,
                    paxScript = paxScriptAbsolutePath,
                    versionFile = versionFileAbsolutePath,
                    workspace = workspacePaths?.WorkspaceFolderPath,
                    recipes = workspacePaths?.RecipesDir,
                    cooks = workspacePaths?.CooksDir,
                    database = workspacePaths?.DatabaseFile,
                },
                runtime = new
                {
                    brokerProcessId = Environment.ProcessId,
                    brokerPort      = port,
                    startedAtUtc    = brokerStartedAtUtc.ToString("O"),
                    transport       = "loopback-http",
                    bindAddress     = "127.0.0.1",
                    implementation  = "native",
                },
                brokerSession = new
                {
                    // brokerSession is deferred -- see Stage 3c
                    // record. Placeholder surfaces explicitly.
                    sessionId             = (string?)null,
                    startedAtUtc          = brokerStartedAtUtc.ToString("O"),
                    startupClassification = "native_stage_3c_partial",
                },
                updateReadiness = new
                {
                    updaterAvailable        = false,
                    latestKnownCookbookVersion = versionInfo.CookbookVersion,
                    upToDate                = (bool?)null,
                    checkPerformedAt        = (string?)null,
                    lastCheckSource         = "bundled-manifest",
                },
            });
        });
    }
}
