namespace PAXCookbookSetup.Gui;

// Installs Python by downloading a known-good stable python.org installer and
// running it silently in PER-USER mode (InstallAllUsers=0) — no administrator
// rights required, consistent with PAX Cookbook's per-user install model. A
// failed/declined install is reported (Failed) and the wizard warns + continues.
//
// Unlike PowerShell 7, python.org has no GitHub-style "latest" API, so a
// known-good version URL is pinned here (updated periodically). The prompt
// explicitly authorizes hard-coding a known-good 3.12.x. The URL host is
// still validated against the download allow-list before any request.
//
// BuildArguments + IsInstallSuccess are pure static helpers (unit-tested).
public sealed class PythonInstaller : IPrerequisiteInstaller
{
    public PrerequisiteKind Kind => PrerequisiteKind.Python;

    // Pinned known-good stable release (python.org, allow-listed host).
    public const string InstallerUrl =
        "https://www.python.org/ftp/python/3.12.8/python-3.12.8-amd64.exe";

    private const int InstallTimeoutMs = 10 * 60 * 1000; // 10 minutes

    private readonly IPrereqDownloader _downloader;
    private readonly ISilentLauncher _launcher;
    private readonly PrerequisiteDetector _detector;

    public PythonInstaller(IPrereqDownloader downloader, ISilentLauncher launcher,
                           PrerequisiteDetector detector)
    {
        _downloader = downloader;
        _launcher = launcher;
        _detector = detector;
    }

    // -----------------------------------------------------------------
    // Pure helpers (unit-tested)
    // -----------------------------------------------------------------

    // Silent, PER-USER install, added to PATH, no test suite. No admin needed.
    public static string BuildInstallerArguments() =>
        "/quiet InstallAllUsers=0 PrependPath=1 Include_test=0";

    // The python.org installer is a wrapped bundle using MSI-style exit codes:
    // 0 = success, 3010 = success + reboot required.
    public static bool IsInstallSuccess(int exitCode) => exitCode == 0 || exitCode == 3010;

    // -----------------------------------------------------------------
    // Orchestration
    // -----------------------------------------------------------------
    public PrerequisiteInstallResult Install(string tempDir, Action<string> progress)
    {
        if (_detector.DetectPython().Satisfied)
            return PrerequisiteInstallResult.AlreadyPresent("Python is already installed.");

        if (!PrereqDownloadHosts.IsAllowed(InstallerUrl))
            return PrerequisiteInstallResult.Failed("Could not resolve a trusted Python download URL.");

        progress("Downloading Python…");
        string installerPath;
        try
        {
            Directory.CreateDirectory(tempDir);
            installerPath = Path.Combine(tempDir, "python-amd64.exe");
        }
        catch (Exception ex)
        {
            return PrerequisiteInstallResult.Failed("Could not prepare the download folder: " + ex.Message);
        }

        if (!_downloader.DownloadFile(InstallerUrl, installerPath))
            return PrerequisiteInstallResult.Failed("Failed to download the Python installer.");

        progress("Installing Python…");
        var run = _launcher.RunAndWait(installerPath, BuildInstallerArguments(), InstallTimeoutMs);

        TryDelete(installerPath);

        if (!run.Started)
            return PrerequisiteInstallResult.Failed("Could not start the Python installer: " + run.Error);
        if (!IsInstallSuccess(run.ExitCode))
            return PrerequisiteInstallResult.Failed($"The Python installer exited with code {run.ExitCode}.");

        progress("Verifying Python…");
        if (_detector.DetectPython().Satisfied)
            return PrerequisiteInstallResult.Installed("Python installed successfully.");

        return PrerequisiteInstallResult.Installed(
            "Python was installed. You may need to sign out and back in for it to be detected.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
