namespace PAXCookbookSetup.Gui;

using System.Runtime.InteropServices;

// Installer for the ASP.NET Core 8 Runtime. PAX Cookbook's broker hosts an
// in-process Kestrel server, so the app's runtimeconfig requires the
// Microsoft.AspNetCore.App shared framework. The .NET 8 Desktop Runtime only
// provides Microsoft.NETCore.App + Microsoft.WindowsDesktop.App — NOT
// Microsoft.AspNetCore.App — so without this prerequisite the app fails to
// launch with: Framework: 'Microsoft.AspNetCore.App' ... No frameworks were
// found. (A dev machine "works" only because the .NET SDK bundles ASP.NET Core.)
public sealed class AspNetCoreRuntimeInstaller : IPrerequisiteInstaller
{
    public PrerequisiteKind Kind => PrerequisiteKind.AspNetCoreRuntime;

    private const string RuntimeVersion = "8.0.28";

    // Official Microsoft download location for the ASP.NET Core Runtime,
    // selected for the machine architecture. The host
    // (builds.dotnet.microsoft.com) is allow-listed. The architecture token MUST
    // match the OS so the native dotnet.exe host can load the framework — an x64
    // runtime on an ARM64 machine installs under Program Files (x86)\dotnet and
    // the native ARM64 host then reports "No frameworks were found".
    public static string BuildDownloadUrl(Architecture arch) =>
        $"https://builds.dotnet.microsoft.com/dotnet/aspnetcore/Runtime/{RuntimeVersion}/" +
        $"aspnetcore-runtime-{RuntimeVersion}-win-{ArchToken(arch)}.exe";

    // The ASP.NET Core Runtime ships win-x64, win-x86 and win-arm64 installers;
    // we install arm64 on ARM64 machines and x64 everywhere else.
    private static string ArchToken(Architecture arch)
        => arch == Architecture.Arm64 ? "arm64" : "x64";

    // Convenience accessor for the current machine architecture.
    public static string DownloadUrl => BuildDownloadUrl(PrereqArch.Os);

    private const int InstallTimeoutMs = 5 * 60 * 1000; // 5 minutes

    private readonly IPrereqDownloader _downloader;
    private readonly IElevatedLauncher _elevated;
    private readonly PrerequisiteDetector _detector;

    public AspNetCoreRuntimeInstaller(IPrereqDownloader downloader, IElevatedLauncher elevated,
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
        // Skip the whole flow if the ASP.NET Core 8 Runtime is already present.
        if (_detector.DetectAspNetCoreRuntime().Satisfied)
        {
            PrereqLog.Write("[PREREQ] ASP.NET Core install: already present, skipping.");
            return PrerequisiteInstallResult.AlreadyPresent("ASP.NET Core 8 Runtime is already installed.");
        }

        var arch = PrereqArch.Os;
        var archToken = ArchToken(arch);
        var url = BuildDownloadUrl(arch);
        PrereqLog.Write($"[PREREQ] ASP.NET Core install: arch={arch} rid={archToken}");
        PrereqLog.Write($"[PREREQ] ASP.NET Core download URL = {url}");

        if (!PrereqDownloadHosts.IsAllowed(url))
        {
            PrereqLog.Write("[PREREQ] ASP.NET Core install: URL rejected by host allow-list - aborting.");
            return PrerequisiteInstallResult.Failed("Could not resolve a trusted ASP.NET Core Runtime download URL.");
        }

        progress($"Downloading ASP.NET Core 8 Runtime ({archToken})…");
        string exePath;
        try
        {
            Directory.CreateDirectory(tempDir);
            exePath = Path.Combine(tempDir, $"aspnetcore-runtime-{RuntimeVersion}-win-{archToken}.exe");
            PrereqLog.Write("[PREREQ] ASP.NET Core download: started.");
            var downloaded = _downloader.DownloadFile(url, exePath);
            PrereqLog.Write($"[PREREQ] ASP.NET Core download: completed (returned {downloaded}).");
        }
        catch (Exception ex)
        {
            PrereqLog.Write($"[PREREQ] ASP.NET Core download: FAILED - {ex.Message}");
            return PrerequisiteInstallResult.Failed($"Failed to download ASP.NET Core 8 Runtime: {ex.Message}");
        }

        progress($"Installing ASP.NET Core 8 Runtime ({archToken})…");
        var result = _elevated.RunElevatedAndWait(exePath, "/install /quiet /norestart", InstallTimeoutMs);
        PrereqLog.Write($"[PREREQ] ASP.NET Core install: exit code = {result.ExitCode} (declined={result.UserDeclined}, error={result.Error ?? "none"})");

        try { File.Delete(exePath); } catch { /* ignore cleanup errors */ }

        if (result.UserDeclined)
            return PrerequisiteInstallResult.Declined("Administrator approval was declined for ASP.NET Core 8 Runtime installation.");

        if (result.Error is not null)
            return PrerequisiteInstallResult.Failed($"Failed to install ASP.NET Core 8 Runtime: {result.Error}");

        if (result.ExitCode == 0)
            return PrerequisiteInstallResult.Installed("ASP.NET Core 8 Runtime was installed successfully.");

        // Non-zero exit code. Most common: (3010) reboot required; treat as success
        // since the runtime is functional and we tell the user to restart later.
        if (result.ExitCode == 3010)
            return PrerequisiteInstallResult.Installed(
                "ASP.NET Core 8 Runtime was installed. You may need to restart your computer for it to take effect.");

        return PrerequisiteInstallResult.Failed($"The ASP.NET Core 8 Runtime installer failed with exit code {result.ExitCode}.");
    }
}
