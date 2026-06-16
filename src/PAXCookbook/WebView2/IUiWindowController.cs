namespace PAXCookbook.WebView2;

// Marshals UI verbs (from the IPC server or in-process callers) onto the
// host window. Implementations marshal calls back to the UI thread.
public interface IUiWindowController
{
    // Restore from minimized, bring to front, activate. Idempotent.
    void FocusWindow();

    // Close the host window WITHOUT prompting the close dialog. Used by
    // PAXCookbook.exe stop / IPC stop verb so the user is not prompted
    // a second time.
    void CloseWindowSilently();
}

// Sink populated by IUiHost during Launch. Set once when the window is
// ready; the primary IPC handler reads it to dispatch open/reopen/stop.
public sealed class UiWindowControllerSink
{
    private IUiWindowController? _controller;
    private readonly ManualResetEventSlim _ready = new(false);

    public void Set(IUiWindowController controller)
    {
        _controller = controller;
        _ready.Set();
    }

    public IUiWindowController? Get() => _controller;

    public bool WaitForReady(TimeSpan timeout) => _ready.Wait(timeout);
}
