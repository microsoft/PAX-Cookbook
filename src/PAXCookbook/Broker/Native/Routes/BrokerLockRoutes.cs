using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PAXCookbook.Broker.Native.Models;
using PAXCookbook.Broker.Native.Services;

namespace PAXCookbook.Broker.Native.Routes;

// Broker lock + readiness surface.
//
//   GET  /api/v1/broker/lock-state -- lock-state snapshot for the SPA.
//                                     Includes lazy inactivity sweep
//                                     so a Locked verdict caused by
//                                     idle timeout is observable on
//                                     the same call. DOES NOT bump
//                                     activity (poller endpoint).
//   POST /api/v1/broker/lock       -- idempotent explicit relock.
//                                     Always succeeds.
//   POST /api/v1/broker/unlock     -- drives an interactive Windows
//                                     Hello / PIN prompt via the
//                                     WindowsReAuthSidecarVerifier
//                                     (hidden one-shot pwsh sidecar
//                                     dot-sourcing Auth\WindowsReAuth.ps1
//                                     -> WinRT UserConsentVerifier).
//                                     On a Verified verdict the
//                                     broker transitions the lock
//                                     service to Unlocked and returns
//                                     a success envelope. Every
//                                     non-Verified verdict surfaces
//                                     as a deterministic envelope
//                                     with a stable reason code the
//                                     SPA discriminates on.
public static class BrokerLockRoutes
{
    public static void Register(
        IEndpointRouteBuilder    app,
        BrokerLockService        lockService,
        IWindowsReAuthVerifier?  verifier)
    {
        app.MapGet("/api/v1/broker/lock-state", () =>
        {
            var s = lockService.GetSnapshot();
            return Results.Json(new
            {
                state                      = s.State,
                lastActivityUtc            = s.LastActivityUtc,
                inactivityTimeoutMinutes   = s.InactivityTimeoutMinutes,
                inactivityRemainingSeconds = s.InactivityRemainingSeconds,
                timeAnomaly                = s.TimeAnomaly,
            });
        });

        app.MapPost("/api/v1/broker/lock", () =>
        {
            var s = lockService.TransitionToLocked();
            return Results.Json(new
            {
                ok                         = true,
                state                      = s.State,
                lastActivityUtc            = s.LastActivityUtc,
                inactivityTimeoutMinutes   = s.InactivityTimeoutMinutes,
                inactivityRemainingSeconds = s.InactivityRemainingSeconds,
                timeAnomaly                = s.TimeAnomaly,
            });
        });

        app.MapPost("/api/v1/broker/unlock", async () =>
        {
            Trace.WriteLine("[BrokerLockRoutes] /api/v1/broker/unlock invoked");

            if (verifier is null)
            {
                Trace.WriteLine("[BrokerLockRoutes] verifier unavailable -- returning 503 device-not-present");
                return Results.Json(new
                {
                    ok                 = false,
                    unlocked           = false,
                    reason             = "device-not-present",
                    message            = "Windows Hello is not available on this device.",
                    verificationResult = "DeviceNotPresent",
                    state              = lockService.GetSnapshot().State,
                },
                statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            WindowsReAuthVerdict verdict;
            try
            {
                verdict = await verifier.VerifyAsync(
                    opClass: "BrokerUnlock",
                    message: "Authenticate to unlock PAX Cookbook.");
            }
            catch (Exception ex)
            {
                Trace.WriteLine("[BrokerLockRoutes] VerifyAsync threw: " + ex.GetType().Name + ": " + ex.Message);
                return Results.Json(new
                {
                    ok                 = false,
                    unlocked           = false,
                    reason             = "verification-failed",
                    message            = "Authentication failed. Try again.",
                    verificationResult = "ComInteropFailure",
                    state              = lockService.GetSnapshot().State,
                },
                statusCode: StatusCodes.Status500InternalServerError);
            }

            Trace.WriteLine("[BrokerLockRoutes] verifier verdict=" + verdict.Result);

            if (verdict.IsVerified)
            {
                var s = lockService.TransitionToUnlocked();
                return Results.Json(new
                {
                    ok                 = true,
                    unlocked           = true,
                    method             = "windows-reauth",
                    verificationResult = "Verified",
                    state              = s.State,
                });
            }

            return verdict.Result switch
            {
                "Canceled" => Results.Json(new
                {
                    ok                 = false,
                    unlocked           = false,
                    reason             = "canceled",
                    message            = "Authentication canceled.",
                    verificationResult = "Canceled",
                    state              = lockService.GetSnapshot().State,
                }, statusCode: StatusCodes.Status401Unauthorized),

                "DeviceNotPresent" or "NotConfiguredForUser" or "DisabledByPolicy" => Results.Json(new
                {
                    ok                 = false,
                    unlocked           = false,
                    reason             = "device-not-present",
                    message            = "Windows Hello is not available on this device.",
                    verificationResult = verdict.Result,
                    state              = lockService.GetSnapshot().State,
                }, statusCode: StatusCodes.Status503ServiceUnavailable),

                _ => Results.Json(new
                {
                    ok                 = false,
                    unlocked           = false,
                    reason             = "verification-failed",
                    message            = "Authentication failed. Try again.",
                    verificationResult = verdict.Result,
                    state              = lockService.GetSnapshot().State,
                }, statusCode: StatusCodes.Status500InternalServerError),
            };
        });
    }
}
