using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3g -- scheduler-registrar abstraction. The native broker
// never calls *-ScheduledTask cmdlets in-process. Instead, it spawns
// the external helper at app\install\Register-PAXScheduledRecipe.ps1
// (the SOLE file in the codebase permitted to touch Task Scheduler),
// passing NON-SECRET argv only. This interface is the seam that
// keeps that policy enforceable: in production WindowsScheduled
// TaskRegistrar performs the spawn; in tests FakeScheduledTaskRegistrar
// records the request and returns a canned result without launching
// any process.
//
// PUT/DELETE routes do NOT consume this interface in Stage 3g --
// those routes return controlled 501 responses until Stage 3h lands
// the WebAuthn re-auth verifier, the projection-hash composer, and
// the Windows Credential Manager writer. The registrar is wired now
// so the abstraction is ready when those prerequisites land.
public interface IScheduledTaskRegistrar
{
    // Spawn / dispatch the registrar with the supplied request. The
    // implementation MUST honour the argv contract:
    //   pwsh.exe -NoProfile -NoLogo -NonInteractive
    //     -File <appRoot>\install\Register-PAXScheduledRecipe.ps1
    //     -Action register|unregister
    //     -WorkspacePath <ws>
    //     -RecipeId <rid>
    //     -ScheduledTaskId <stid>
    //     [-RecurrenceJson <json>]   (register only)
    // Stdout/stderr MUST NOT be silently dropped -- callers use them
    // to surface the registrar's diagnostic output in the broker's
    // PUT/DELETE response when the registrar exits non-zero. The
    // implementation MUST NOT pass a client secret on argv under any
    // circumstance.
    Task<ScheduledTaskRegistrarResult> InvokeAsync(
        ScheduledTaskRegistrarRequest request,
        CancellationToken cancellationToken = default);
}
