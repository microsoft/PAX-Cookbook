using System.Text.Json;

namespace PAXCookbookSetup.Gui;

// Installs PowerShell 7 by downloading the latest stable win-x64 MSI from
// Microsoft's PowerShell GitHub releases and running it silently under an
// elevated (UAC) child process. The MSI requires administrator rights; a
// declined UAC prompt is reported as UserDeclined so the wizard warns and
// continues (PAX Cookbook still installs; bakes need PowerShell 7).
//
// The URL-selection (GitHub release JSON -> win-x64 .msi) and the msiexec
// argument construction are PURE static helpers so they are unit-tested
// without any network or process. Network + elevation go through the
// IPrereqDownloader / IElevatedLauncher seams.
public sealed class PowerShell7Installer : IPrerequisiteInstaller
{
    public PrerequisiteKind Kind => PrerequisiteKind.PowerShell7;

    public const string ReleasesApiUrl =
        "https://api.github.com/repos/PowerShell/PowerShell/releases/latest";

    // Known-good fallback used only when the GitHub API is unreachable or
    // returns no usable asset. A real, allow-listed GitHub release asset URL.
    public const string FallbackMsiUrl =
        "https://github.com/PowerShell/PowerShell/releases/download/v7.4.6/PowerShell-7.4.6-win-x64.msi";

    private const int InstallTimeoutMs = 10 * 60 * 1000; // 10 minutes

    private readonly IPrereqDownloader _downloader;
    private readonly IElevatedLauncher _elevated;
    private readonly PrerequisiteDetector _detector;

    public PowerShell7Installer(IPrereqDownloader downloader, IElevatedLauncher elevated,
                                PrerequisiteDetector detector)
    {
        _downloader = downloader;
        _elevated = elevated;
        _detector = detector;
    }

    // -----------------------------------------------------------------
    // Pure helpers (unit-tested)
    // -----------------------------------------------------------------

    // Returns the browser_download_url of the first asset whose name ends with
    // "-win-x64.msi" (the per-machine x64 installer), or null when the JSON is
    // missing/malformed or contains no such asset.
    public static string? TrySelectWinX64MsiUrl(string? releaseJson)
    {
        if (string.IsNullOrWhiteSpace(releaseJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(releaseJson);
            if (!doc.RootElement.TryGetProperty("assets", out var assets)
                || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameEl)) continue;
                var name = nameEl.GetString();
                if (string.IsNullOrEmpty(name)) continue;
                if (!name.EndsWith("-win-x64.msi", StringComparison.OrdinalIgnoreCase)) continue;

                if (asset.TryGetProperty("browser_download_url", out var urlEl))
                {
                    var url = urlEl.GetString();
                    if (PrereqDownloadHosts.IsAllowed(url)) return url;
                }
            }
        }
        catch { /* malformed JSON */ }
        return null;
    }

    // Silent, per-machine install with the consumer-friendly options the prompt
    // specifies (no shell context menus, no remoting; register the manifest).
    public static string BuildMsiArguments(string msiPath) =>
        $"/i \"{msiPath}\" /qn /norestart " +
        "ADD_EXPLORER_CONTEXT_MENU_OPENPOWERSHELL=0 " +
        "ADD_FILE_CONTEXT_MENU_RUNPOWERSHELL=0 " +
        "ENABLE_PSREMOTING=0 REGISTER_MANIFEST=1";

    // MSI success: 0 (ok) or 3010 (ok, reboot required).
    public static bool IsMsiSuccess(int exitCode) => exitCode == 0 || exitCode == 3010;

    // -----------------------------------------------------------------
    // Orchestration
    // -----------------------------------------------------------------
    public PrerequisiteInstallResult Install(string tempDir, Action<string> progress)
    {
        // Skip the whole flow if a satisfying PowerShell 7 is already present.
        if (_detector.DetectPowerShell7().Satisfied)
            return PrerequisiteInstallResult.AlreadyPresent("PowerShell 7 is already installed.");

        progress("Finding the latest PowerShell 7…");
        var json = _downloader.GetText(ReleasesApiUrl, accept: "application/vnd.github+json");
        var url = TrySelectWinX64MsiUrl(json) ?? FallbackMsiUrl;
        if (!PrereqDownloadHosts.IsAllowed(url))
            return PrerequisiteInstallResult.Failed("Could not resolve a trusted PowerShell 7 download URL.");

        progress("Downloading PowerShell 7…");
        string msiPath;
        try
        {
            Directory.CreateDirectory(tempDir);
            msiPath = Path.Combine(tempDir, "PowerShell-win-x64.msi");
        }
        catch (Exception ex)
        {
            return PrerequisiteInstallResult.Failed("Could not prepare the download folder: " + ex.Message);
        }

        if (!_downloader.DownloadFile(url, msiPath))
            return PrerequisiteInstallResult.Failed("Failed to download the PowerShell 7 installer.");

        progress("Installing PowerShell 7 (administrator approval required)…");
        var run = _elevated.RunElevatedAndWait("msiexec.exe", BuildMsiArguments(msiPath), InstallTimeoutMs);

        TryDelete(msiPath);

        if (run.UserDeclined)
            return PrerequisiteInstallResult.Declined(
                "PowerShell 7 was not installed because administrator approval was declined. " +
                "You can install it later from https://aka.ms/powershell.");
        if (!run.Started)
            return PrerequisiteInstallResult.Failed("Could not start the PowerShell 7 installer: " + run.Error);
        if (!IsMsiSuccess(run.ExitCode))
            return PrerequisiteInstallResult.Failed($"The PowerShell 7 installer exited with code {run.ExitCode}.");

        progress("Verifying PowerShell 7…");
        if (_detector.DetectPowerShell7().Satisfied)
            return PrerequisiteInstallResult.Installed("PowerShell 7 installed successfully.");

        // Installed but not yet visible (often a fresh PATH not seen by this
        // process). Treat as success-with-note rather than a hard failure.
        return PrerequisiteInstallResult.Installed(
            "PowerShell 7 was installed. You may need to sign out and back in for it to be detected.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
