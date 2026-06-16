using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Routes;

// Stage 3c -- expanded GET /api/v1/health. Mirrors a controlled
// subset of the PowerShell broker's Get-HealthPayload
// (Start-Broker.ps1 L3548-L3795). The native broker has not absorbed
// the PowerShell broker's lifecycle state yet (no recent-error ring,
// no package-trust counters, no broker-session evidence map), so the
// envelope ONLY surfaces what the native broker can compute
// deterministically. Doing so deliberately -- "do not pretend partial
// parity is full parity" (Brian, Stage 3c authorization).
public static class HealthRoutes
{
    public static void Register(
        IEndpointRouteBuilder app,
        string brokerImplementation,
        int port,
        WorkspacePaths? workspacePaths,
        DateTimeOffset brokerStartedAtUtc,
        Func<DateTimeOffset> utcNow)
    {
        app.MapGet("/api/v1/health", () =>
        {
            var nowUtc = utcNow();
            var uptimeSeconds = Math.Max(0d, (nowUtc - brokerStartedAtUtc).TotalSeconds);
            long? dbSizeBytes = null;
            if (workspacePaths is not null
                && File.Exists(workspacePaths.DatabaseFile))
            {
                try
                {
                    dbSizeBytes = new FileInfo(workspacePaths.DatabaseFile).Length;
                }
                catch
                {
                    // Deliberately swallowed -- a transient FS error
                    // for size measurement must not 500 the health
                    // route.
                }
            }

            return Results.Json(new
            {
                status = "ok",
                broker = brokerImplementation,
                pid = Environment.ProcessId,
                port,
                startedAtUtc = brokerStartedAtUtc.ToString("O"),
                uptimeSeconds,
                workspaceFolderPath = workspacePaths?.WorkspaceFolderPath,
                databaseFilePath = workspacePaths?.DatabaseFile,
                databaseFileExists = workspacePaths is not null
                    && File.Exists(workspacePaths.DatabaseFile),
                dbSizeBytes,
                // Native broker has no session-evidence subsystem yet
                // (deferred to Stage 3d+). Surface explicit placeholder
                // markers so a side-by-side diff against the PowerShell
                // broker is obvious instead of silent.
                brokerSession = new
                {
                    sessionId = (string?)null,
                    startedAtUtc = brokerStartedAtUtc.ToString("O"),
                    startupClassification = "native_stage_3c_partial",
                },
                timestamp = nowUtc.ToString("O"),
            });
        });
    }
}
