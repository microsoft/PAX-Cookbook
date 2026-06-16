namespace PAXCookbook.WebView2;

// Native close dialog per native-close-lifecycle-contract.md §4
// (Stage 3k two-button revision). The original Phase 7 contract
// permitted a third "Close app only" choice that left a separate
// pwsh.exe broker process serving on its port. Stage 3j moved
// Kestrel in-process, so the WebView2 window and the broker share
// PAXCookbook.exe's lifetime. Closing the only message loop
// terminates the broker. Exposing CloseAppOnly would lie about
// behavior, so the user-facing choice space is now just Cancel
// and ClosePaxCookbook.
public enum CloseChoice
{
    Cancel,
    ClosePaxCookbook
}

public enum CloseTrigger
{
    TitleBarX,
    AltF4,
    SystemMenu,
    Taskbar,
    WmClose,
    Stop
}

public interface ICloseDialogService
{
    // ownerHwnd is the HWND of the WebView2 host window so the dialog
    // is window-modal (not system-modal). May be IntPtr.Zero for tests.
    CloseChoice Prompt(IntPtr ownerHwnd);
}

// Test fake that always returns a fixed choice.
public sealed class FixedChoiceCloseDialog : ICloseDialogService
{
    public CloseChoice Choice { get; set; }
    public int Calls { get; private set; }
    public FixedChoiceCloseDialog(CloseChoice choice) => Choice = choice;
    public CloseChoice Prompt(IntPtr ownerHwnd) { Calls++; return Choice; }
}
