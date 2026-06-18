using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Gui;

namespace PAXCookbookSetup;

// Detects and installs the three REQUIRED prerequisites (.NET 8 Desktop
// Runtime, PowerShell 7, Python) for a CLI `install`, mirroring the GUI
// wizard's Progress-screen flow (SetupWizardForm): detect via
// PrerequisiteDetector, then install any missing via PrerequisiteCoordinator,
// reusing the SAME installers + system probe. Status is written to the console.
// UAC elevation is prompted by the underlying elevated launchers exactly as in
// the GUI; the CLI cannot show a Retry/Exit dialog, so a failed or declined
// prerequisite aborts Setup (the user can re-run).
internal static class CliPrerequisites
{
    // Returns SetupExitCodes.Ok when every required prerequisite is satisfied,
    // or a non-zero exit code when one of them could not be installed.
    public static int Ensure(SetupLogger log)
    {
        PrereqLog.Begin();
        var detector = new PrerequisiteDetector(new SystemPrerequisiteProbe());
        var dotnet = detector.DetectDotNet8DesktopRuntime();
        var aspnet = detector.DetectAspNetCoreRuntime();
        var ps7 = detector.DetectPowerShell7();
        var python = detector.DetectPython();

        Console.WriteLine();
        Console.WriteLine("Checking prerequisites...");
        Console.WriteLine("  " + StatusLine(dotnet));
        Console.WriteLine("  " + StatusLine(aspnet));
        Console.WriteLine("  " + StatusLine(ps7));
        Console.WriteLine("  " + StatusLine(python));

        var needed = new Dictionary<PrerequisiteKind, bool>
        {
            [PrerequisiteKind.DotNet8DesktopRuntime] = !dotnet.Satisfied,
            [PrerequisiteKind.AspNetCoreRuntime] = !aspnet.Satisfied,
            [PrerequisiteKind.PowerShell7] = !ps7.Satisfied,
            [PrerequisiteKind.Python] = !python.Satisfied
        };

        log.Write("cli-prereq-detected", fields: new Dictionary<string, object?>
        {
            ["dotnet8"] = dotnet.Satisfied,
            ["aspnetcore"] = aspnet.Satisfied,
            ["powershell7"] = ps7.Satisfied,
            ["python"] = python.Satisfied
        });

        if (!needed.Values.Any(v => v))
            return SetupExitCodes.Ok;

        Console.WriteLine();
        Console.WriteLine("Installing missing prerequisites...");

        string tempDir = Path.Combine(Path.GetTempPath(), "PAXSetup_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var downloader = new HttpPrereqDownloader();
            var coordinator = new PrerequisiteCoordinator(new IPrerequisiteInstaller[]
            {
                new DotNet8DesktopRuntimeInstaller(downloader, new RealElevatedLauncher(), detector),
                new AspNetCoreRuntimeInstaller(downloader, new RealElevatedLauncher(), detector),
                new PowerShell7Installer(downloader, new RealElevatedLauncher(), detector),
                new PythonInstaller(downloader, new RealSilentLauncher(), detector)
            });

            var result = coordinator.Run(needed, tempDir,
                progress: msg => Console.WriteLine("  " + msg),
                onError: (kind, message) =>
                {
                    Console.Error.WriteLine($"  {DisplayName(kind)}: {message}");
                    return RetryExitDecision.ExitSetup;
                });

            foreach (var r in result.Results)
                Console.WriteLine($"  {r.DisplayName}: {Describe(r.Result.Outcome)}");

            bool allSatisfied = result.Results.All(r => r.Result.Satisfied);
            log.Write("cli-prereq-result", allSatisfied ? "info" : "error",
                new Dictionary<string, object?>
                {
                    ["cancelled"] = result.IsCancelled,
                    ["allSatisfied"] = allSatisfied
                });

            if (result.IsCancelled || !allSatisfied)
            {
                Console.Error.WriteLine(
                    "A required prerequisite could not be installed. Setup cannot continue.");
                return SetupExitCodes.InstallFailed;
            }
            return SetupExitCodes.Ok;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private static string StatusLine(PrerequisiteStatus s)
    {
        if (s.Satisfied)
            return s.DetectedVersion is { Length: > 0 }
                ? $"{s.DisplayName}: found ({s.DetectedVersion})"
                : $"{s.DisplayName}: found";
        return $"{s.DisplayName}: not found - installing...";
    }

    private static string DisplayName(PrerequisiteKind kind) => kind switch
    {
        PrerequisiteKind.DotNet8DesktopRuntime => ".NET 8 Desktop Runtime",
        PrerequisiteKind.AspNetCoreRuntime => "ASP.NET Core 8 Runtime",
        PrerequisiteKind.PowerShell7 => "PowerShell 7",
        PrerequisiteKind.Python => "Python",
        _ => kind.ToString()
    };

    private static string Describe(PrerequisiteInstallOutcome o) => o switch
    {
        PrerequisiteInstallOutcome.Installed => "installed",
        PrerequisiteInstallOutcome.AlreadyPresent => "already present",
        PrerequisiteInstallOutcome.UserDeclined => "declined (required)",
        PrerequisiteInstallOutcome.Cancelled => "cancelled",
        _ => "not installed"
    };
}
