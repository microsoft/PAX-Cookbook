namespace PAXCookbookSetup.Gui;

using System.Runtime.InteropServices;

// Installer for the .NET 8 Desktop Runtime. The Setup exe stays self-contained
// (it must run without .NET), but the PAX Cookbook app itself is now framework-
// dependent (uses .NET 8 from the system). This installer handles the one-time
// setup of the .NET 8 Desktop Runtime on first install.
public sealed class DotNet8DesktopRuntimeInstaller : IPrerequisiteInstaller
{
    public PrerequisiteKind Kind => PrerequisiteKind.DotNet8DesktopRuntime;

    private const string RuntimeVersion = "8.0.11";

    // Official Microsoft download location for the .NET 8 Desktop Runtime,
    // selected for the machine architecture. A known-good version (8.0.11) is
    // pinned to avoid breaking on future build variations; the host
    // (builds.dotnet.microsoft.com) is allow-listed. The architecture token MUST
    // match the OS: an x64 runtime on an ARM64 machine installs under
    // Program Files (x86)\dotnet, and the native ARM64 dotnet.exe host at
    // Program Files\dotnet then reports "No frameworks were found".
    public static string BuildDownloadUrl(Architecture arch) =>
        $"https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/{RuntimeVersion}/" +
        $"windowsdesktop-runtime-{RuntimeVersion}-win-{ArchToken(arch)}.exe";

    // The .NET 8 Desktop Runtime ships win-x64, win-x86 and win-arm64 installers;
    // we install arm64 on ARM64 machines and x64 everywhere else.
    private static string ArchToken(Architecture arch)
        => arch == Architecture.Arm64 ? "arm64" : "x64";

    // Convenience accessor for the current machine architecture.
    public static string DownloadUrl => BuildDownloadUrl(PrereqArch.Os);

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

        var arch = PrereqArch.Os;
        var archToken = ArchToken(arch);
        var url = BuildDownloadUrl(arch);
        PrereqLog.Write($"[PREREQ] .NET 8 install: arch={arch} rid={archToken}");
        PrereqLog.Write($"[PREREQ] .NET 8 download URL = {url}");

        if (!PrereqDownloadHosts.IsAllowed(url))
        {
            PrereqLog.Write("[PREREQ] .NET 8 install: URL rejected by host allow-list - aborting.");
            return PrerequisiteInstallResult.Failed("Could not resolve a trusted .NET 8 Desktop Runtime download URL.");
        }

        progress($"Downloading .NET 8 Desktop Runtime ({archToken})…");
        string exePath;
        try
        {
            Directory.CreateDirectory(tempDir);
            exePath = Path.Combine(tempDir, $"windowsdesktop-runtime-{RuntimeVersion}-win-{archToken}.exe");
            PrereqLog.Write("[PREREQ] .NET 8 download: started.");
            var downloaded = _downloader.DownloadFile(url, exePath);
            PrereqLog.Write($"[PREREQ] .NET 8 download: completed (returned {downloaded}).");
        }
        catch (Exception ex)
        {
            PrereqLog.Write($"[PREREQ] .NET 8 download: FAILED - {ex.Message}");
            return PrerequisiteInstallResult.Failed($"Failed to download .NET 8 Desktop Runtime: {ex.Message}");
        }

        progress($"Installing .NET 8 Desktop Runtime ({archToken})…");
        var result = _elevated.RunElevatedAndWait(exePath, "/install /quiet /norestart", InstallTimeoutMs);
        PrereqLog.Write($"[PREREQ] .NET 8 install: exit code = {result.ExitCode} (declined={result.UserDeclined}, error={result.Error ?? "none"})");
        
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
