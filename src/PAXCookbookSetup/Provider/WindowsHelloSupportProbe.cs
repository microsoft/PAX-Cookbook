using System;

namespace PAXCookbookSetup.Provider;

// Setup-side Windows Hello support probe.
//
// Setup can only make a best-effort determination without launching the full
// app: the actual Windows Hello gesture (WebAuthn platform authenticator) runs
// later in the app. Setup already gates the supported Windows version before the
// wizard runs, so on a supported OS Windows Hello is treated as available here.
// The interface exists so the wizard and tests can substitute the result.
public sealed class WindowsHelloSupportProbe : IHelloSupportProbe
{
    private readonly Func<bool> _supportedOs;

    public WindowsHelloSupportProbe(Func<bool>? supportedOs = null)
    {
        // Default: assume the supported-OS gate upstream already passed. Callers
        // may inject a stricter probe as the OS story evolves.
        _supportedOs = supportedOs ?? (static () => OperatingSystem.IsWindowsVersionAtLeast(10));
    }

    public bool IsWindowsHelloAvailable() => _supportedOs();
}
