namespace PAXCookbook.Runtime;

// Reports presence of broker-owned sidecar files under <workspace>\Runtime.
// Read-only; never deletes. Used by status and to detect stale state.
public sealed record SidecarReport(
    bool LockPresent,
    bool TokenPresent,
    bool AcquirePresent,
    bool BrowserWindowPresent,
    bool CloseIntentPresent,
    bool CloseWatcherLockPresent);

public sealed class SidecarReader
{
    public SidecarReport Read(string workspaceFolderPath)
    {
        bool Exists(string name)
        {
            if (string.IsNullOrWhiteSpace(workspaceFolderPath)) return false;
            return File.Exists(Path.Combine(workspaceFolderPath, "Runtime", name));
        }
        return new SidecarReport(
            Exists("workspace.lock"),
            Exists("broker.token"),
            Exists("workspace.lock.acquire"),
            Exists("browser.window.json"),
            Exists("app-close-intent.json"),
            Exists("app-close-watcher.lock"));
    }
}
