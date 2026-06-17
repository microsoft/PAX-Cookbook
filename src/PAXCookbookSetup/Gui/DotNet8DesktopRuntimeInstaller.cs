namespace PAXCookbookSetup.Gui;

// Installer for the .NET 8 Desktop Runtime. The Setup exe stays self-contained
// (it must run without .NET), but the PAX Cookbook app itself is now framework-
// dependent (uses .NET 8 from the system). This installer handles the one-time
// setup of the .NET 8 Desktop Runtime on first install.
public sealed class DotNet8DesktopRuntimeInstaller : IPrerequisiteInstaller
{
    public PrerequisiteKind Kind => PrerequisiteKind.DotNet8DesktopRuntime;

    // Official Microsoft download location for .NET 8 Desktop Runtime. Hardcode
    // a known-good version (8.0.11) to avoid breaking on future CI/build variations.
    // This URL is allow-listed and stable.
    public const string DownloadUrl =
        "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.11/windowsdesktop-runtime-8.0.11-win-x64.exe";

    private const int InstallTimeoutMs = 5 * 60 * 1000; // 5 minutes

    private readonly IPrereqDownloader _downloader;
    private readonly IElevatedLauncher _elevated;
    private readonly PrerequisiteDetector _detector;

    public DotNet8DesktopRuntimeInstaller(IPrereqDownloader downloader, IElevatedLauncher elevated,
                                           PrerequisiteDetector detector)
    {
        _downloader = downloader;
        _elevated = elevated;
        _detector = detector;
    }

    // -----------------------------------------------------------------
    // Orchestration
    // -----------------------------------------------------------------
    public PrerequisiteInstallResult Install(string tempDir, Action<string> progress)
    {
        // Skip the whole flow if .NET 8 Desktop Runtime is already present.
        if (_detector.DetectDotNet8DesktopRuntime().Satisfied)
            return PrerequisiteInstallResult.AlreadyPresent(".NET 8 Desktop Runtime is already installed.");

        if (!PrereqDownloadHosts.IsAllowed(DownloadUrl))
            return PrerequisiteInstallResult.Failed("Could not resolve a trusted .NET 8 Desktop Runtime download URL.");

        progress("Downloading .NET 8 Desktop Runtime…");
        string exePath;
        try
        {
            Directory.CreateDirectory(tempDir);
            exePath = Path.Combine(tempDir, "windowsdesktop-runtime-8.0.11-win-x64.exe");
            _downloader.DownloadFile(DownloadUrl, exePath);
        }
        catch (Exception ex)
        {
            return PrerequisiteInstallResult.Failed($"Failed to download .NET 8 Desktop Runtime: {ex.Message}");
        }

        progress("Installing .NET 8 Desktop Runtime…");
        var result = _elevated.RunElevatedAndWait(exePath, "/install /quiet /norestart", InstallTimeoutMs);
        
        try { File.Delete(exePath); } catch { /* ignore cleanup errors */ }

        if (result.UserDeclined)
            return PrerequisiteInstallResult.Declined("Administrator approval was declined for .NET 8 Desktop Runtime installation.");

        if (result.Error is not null)
            return PrerequisiteInstallResult.Failed($"Failed to install .NET 8 Desktop Runtime: {result.Error}");

        if (result.ExitCode == 0)
            return PrerequisiteInstallResult.Installed(".NET 8 Desktop Runtime was installed successfully.");

        // Non-zero exit code. Most common: (3010) reboot required; treat as success
        // since the runtime is functional and we tell the user to restart later.
        if (result.ExitCode == 3010)
            return PrerequisiteInstallResult.Installed(
                ".NET 8 Desktop Runtime was installed. You may need to restart your computer for it to take effect.");

        return PrerequisiteInstallResult.Failed($"The .NET 8 Desktop Runtime installer failed with exit code {result.ExitCode}.");
    }
}
