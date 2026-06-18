using System;
using System.Runtime.InteropServices;

namespace PAXCookbook.Shared;

// Hides and detaches the host console window for a GUI/background process that
// is launched through the Microsoft-signed dotnet.exe host. dotnet.exe is a
// CONSOLE application, so a shortcut / Run key / protocol / file-association /
// Add-Remove-Programs launch of `dotnet.exe <app-or-setup.dll>` would otherwise
// flash a blank terminal window. Calling HideConsoleWindow() as the very first
// thing the process does hides that window in well under a millisecond (no
// visible flash) and then detaches from the console.
//
// This is the WDAC-safe replacement for the former wscript.exe + launch.vbs /
// uninstall.vbs hidden launchers: kernel32.dll and user32.dll are Windows
// system DLLs that corporate WDAC policies always allow, and no Windows Script
// Host (which strict WDAC policies block) is involved.
public static class ConsoleWindowHelper
{
    private const int SW_HIDE = 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    // Hide the console window (if any) immediately, then detach. Best-effort —
    // never throws, so it can be the first line of an entry point.
    public static void HideConsoleWindow()
    {
        try
        {
            var consoleWindow = GetConsoleWindow();
            if (consoleWindow != IntPtr.Zero)
                ShowWindow(consoleWindow, SW_HIDE);
            FreeConsole();
        }
        catch { /* best effort — never block startup */ }
    }
}
