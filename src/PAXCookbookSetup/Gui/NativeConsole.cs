using System.Runtime.InteropServices;

namespace PAXCookbookSetup.Gui;

// The Setup EXE is now a WinExe (Windows subsystem) so a double-click does
// not flash a console window before the GUI wizard appears. When Setup is
// instead invoked from a terminal with a CLI verb (install/uninstall/etc.),
// a Windows-subsystem process does NOT inherit the parent terminal's
// console, so Console.Out/Error would go nowhere. AttachConsole reconnects
// our standard streams to the launching terminal so scripted/interactive
// CLI use still prints normally. When stdout is redirected (the E2E tests
// and most scripted installs), the redirected pipe is used regardless of
// subsystem, so this is a no-op in that case.
internal static class NativeConsole
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    // Best-effort attach to the parent process's console. Never throws —
    // if there is no parent console (e.g. launched from Explorer with a
    // verb) the CLI simply produces no visible console output, which is
    // acceptable and matches prior behaviour for non-interactive launches.
    public static void AttachToParent()
    {
        try { AttachConsole(ATTACH_PARENT_PROCESS); }
        catch { /* best effort only */ }
    }
}
