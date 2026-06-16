using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3h -- per-operation Windows re-auth gate for the native
// scheduled-task PUT/DELETE routes. The PowerShell broker performs
// this gate via Invoke-WindowsReAuth (Auth\WindowsReAuth.ps1) which
// calls the WinRT UserConsentVerifier (Windows Hello / PIN /
// biometric prompt). The native implementation delegates to a
// hidden one-shot pwsh sidecar that invokes the exact same PS
// function so policy + verdict semantics stay in lock-step. The
// interface is the seam the route handlers consume and tests
// override with FakeWindowsReAuthVerifier.
//
// Tests MUST NEVER hit the real Hello UI; the route resolves the
// verifier from a Stage3hServiceBundle and only the production
// bundle wires WindowsReAuthSidecarVerifier.
public interface IWindowsReAuthVerifier
{
    // Trigger a per-operation Windows re-auth verification.
    //   * opClass:   identifies the policy bucket (scheduleConfig
    //                for scheduled-task PUT/DELETE). Mirrors
    //                $Script:BrokerLockReAuthOpClasses in
    //                Auth\BrokerLock.ps1.
    //   * message:   operator-facing prompt text passed to
    //                Invoke-WindowsReAuth -Message.
    // Returns a verdict; only Result == "Verified" passes. The
    // implementation MUST NOT throw on a non-Verified verdict; it
    // surfaces every PowerShell return value (DeviceNotPresent /
    // Canceled / ComInteropFailure / etc.) so the route can render
    // the correct 401 reAuthRequired envelope.
    Task<WindowsReAuthVerdict> VerifyAsync(
        string opClass,
        string message,
        CancellationToken cancellationToken = default);
}
