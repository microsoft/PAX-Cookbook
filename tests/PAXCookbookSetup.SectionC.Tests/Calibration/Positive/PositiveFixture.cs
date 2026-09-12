using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace SectionC.PositiveFixture;

public static class PositiveFixture
{
    public static void NeverExecuteManagedCalls(string path)
    {
        File.WriteAllText(path, string.Empty);
        using var client = new HttpClient();
        _ = client.GetAsync("https://example.invalid");
        _ = Process.Start(new ProcessStartInfo("cmd.exe"));
        using var store = new X509Store();
        store.Open(OpenFlags.ReadWrite);
    }

    [DllImport("advapi32.dll", EntryPoint = "RegSetValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueEx(IntPtr key, string valueName, uint reserved, uint type, byte[] data, uint dataLength);

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceControlManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(IntPtr message, IntPtr window, uint minimum, uint maximum);
}