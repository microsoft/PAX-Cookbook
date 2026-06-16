using System.Runtime.InteropServices;

namespace PAXCookbook.WebView2;

// AUMID per webview2-host-contract §7. Called BEFORE any window is
// created so taskbar/Start identity attaches to the host process.
public interface IAppUserModelIdSetter
{
    string Aumid { get; }
    bool TrySet();
}

public sealed class Win32AppUserModelIdSetter : IAppUserModelIdSetter
{
    public string Aumid { get; }

    public Win32AppUserModelIdSetter(string aumid = "PAXCookbook.App.v1") => Aumid = aumid;

    public bool TrySet()
    {
        try
        {
            int hr = SetCurrentProcessExplicitAppUserModelID(Aumid);
            return hr >= 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);
}

// Recording test fake: never touches shell32; just records calls.
public sealed class RecordingAumidSetter : IAppUserModelIdSetter
{
    public string Aumid { get; }
    public int Calls { get; private set; }
    public RecordingAumidSetter(string aumid = "PAXCookbook.App.v1") => Aumid = aumid;
    public bool TrySet() { Calls++; return true; }
}
