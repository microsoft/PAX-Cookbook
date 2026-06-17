using System.Diagnostics;

namespace PAXCookbookSetup.Gui;

public sealed record SilentLaunchResult(bool Started, int ExitCode, string? Error)
{
    public static SilentLaunchResult Fail(string error) => new(false, -1, error);
    public static SilentLaunchResult Ran(int exitCode) => new(true, exitCode, null);
}

// Launches a process NON-elevated and waits for it to exit. Used to run the
// Python installer in per-user mode (InstallAllUsers=0), which needs no
// administrator rights — so, unlike IElevatedLauncher, there is NO Verb="runas"
// here and the Setup process's (non-elevated) token is inherited. Keeps PAX
// Cookbook's per-user, no-admin install model intact (constraint 16).
public interface ISilentLauncher
{
    SilentLaunchResult RunAndWait(string fileName, string arguments, int timeoutMs);
}

public sealed class RealSilentLauncher : ISilentLauncher
{
    public SilentLaunchResult RunAndWait(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,   // inherit our (non-elevated) token
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var p = Process.Start(psi);
            if (p is null) return SilentLaunchResult.Fail("The installer process did not start.");
            if (!p.WaitForExit(timeoutMs))
                return SilentLaunchResult.Fail("The installer did not finish in the allotted time.");
            return SilentLaunchResult.Ran(p.ExitCode);
        }
        catch (Exception ex)
        {
            return SilentLaunchResult.Fail(ex.Message);
        }
    }
}
