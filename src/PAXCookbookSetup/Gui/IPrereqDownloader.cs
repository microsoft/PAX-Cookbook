namespace PAXCookbookSetup.Gui;

// Download seam for prerequisite installers. Production uses HttpPrereqDownloader
// (real HTTPS to GitHub / python.org); tests inject a fake so the URL-selection
// + orchestration logic is exercised without any network call.
//
// Constraint 1 note: these are USER-TRIGGERED, READ-ONLY outbound fetches of
// PUBLIC installer artifacts at install time (Brian explicitly directed
// "download prerequisites at install time"). No tenant data leaves the machine;
// the broker already performs comparable outbound HTTPS for engine-manifest and
// Pantry GitHub reads. Hosts are allow-listed to GitHub / python.org.
public interface IPrereqDownloader
{
    // GET a small text document (e.g. the GitHub "latest release" JSON).
    // Returns null on any failure. `accept` sets the Accept header when given.
    string? GetText(string url, string? accept = null);

    // Download a binary to destPath. Returns true only when the file was fully
    // written. Implementations must reject non-allow-listed hosts.
    bool DownloadFile(string url, string destPath);
}
