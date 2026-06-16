namespace PAXCookbook.WebView2;

// Phase 7: optional controller sink + close coordinator. Both nullable
// so Phase 6 tests that build a bare UiHostLaunchRequest still work.
public sealed record UiHostLaunchRequest(
    string BrokerUrl,
    int BrokerPort,
    string UserDataFolder,
    string WindowTitle,
    UiWindowControllerSink? ControllerSink = null,
    CloseGestureCoordinator? CloseCoordinator = null);

public enum UiHostOutcome
{
    Launched,
    LaunchFailed
}

public sealed record UiHostResult(UiHostOutcome Outcome, string? FailureDetail);

public interface IUiHost
{
    UiHostResult Launch(UiHostLaunchRequest request);
}

// No-op host used by tests and by command paths that intentionally must
// not show UI. If a controller sink is supplied, a stub controller is
// installed so IPC verb dispatch is exercisable without UI.
public sealed class NullUiHost : IUiHost
{
    public UiHostResult Launch(UiHostLaunchRequest request)
    {
        if (request.ControllerSink is not null)
            request.ControllerSink.Set(new StubController());
        return new(UiHostOutcome.Launched, null);
    }

    private sealed class StubController : IUiWindowController
    {
        public void FocusWindow() { }
        public void CloseWindowSilently() { }
    }
}
