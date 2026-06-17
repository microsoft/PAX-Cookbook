using System.ComponentModel;
using System.Diagnostics;

namespace PAXCookbookSetup.Gui;

public sealed record ElevatedLaunchResult(bool Started, bool UserDeclined, int ExitCode, string? Error)
{
    public static ElevatedLaunchResult Declined() =>
        new(Started: false, UserDeclined: true, ExitCode: -1,
            Error: "Administrator approval was declined.");
    public static ElevatedLaunchResult Fail(string error) =>
        new(Started: false, UserDeclined: false, ExitCode: -1, Error: error);
    public static ElevatedLaunchResult Ran(int exitCode) =>
        new(Started: true, UserDeclined: false, ExitCode: exitCode, Error: null);
}

// Launches a process ELEVATED (UAC prompt) and waits for it to exit. Used only
// to run the PowerShell 7 MSI, which requires administrator rights. The main
// Setup wizard stays non-elevated; only this child is elevated, so PAX
// Cookbook's own install remains per-user (constraint 16: no runtime admin —
// install-time optional elevation for a Microsoft MSI the user explicitly
// approves is separate from the app's normal runtime). A declined UAC prompt
// is reported as UserDeclined (NOT an error) so the wizard can warn + continue.
public interface IElevatedLauncher
{
    ElevatedLaunchResult RunElevatedAndWait(string fileName, string arguments, int timeoutMs);
}

public sealed class RealElevatedLauncher : IElevatedLauncher
{
    public ElevatedLaunchResult RunElevatedAndWait(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,   // required for Verb = "runas"
                Verb = "runas",            // request elevation (UAC)
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            if (p is null) return ElevatedLaunchResult.Fail("The installer process did not start.");
            if (!p.WaitForExit(timeoutMs))
            {
                // Leave the elevated installer running; we just stop waiting.
                return ElevatedLaunchResult.Fail("The installer did not finish in the allotted time.");
            }
            return ElevatedLaunchResult.Ran(p.ExitCode);
        }
        catch (Win32Exception wex) when (wex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — the user dismissed the UAC prompt.
            return ElevatedLaunchResult.Declined();
        }
        catch (Exception ex)
        {
            return ElevatedLaunchResult.Fail(ex.Message);
        }
    }
}
