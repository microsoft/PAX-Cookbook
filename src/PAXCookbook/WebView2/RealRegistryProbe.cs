using Microsoft.Win32;

namespace PAXCookbook.WebView2;

// Production registry probe. Opens base keys explicitly in the
// Registry64 / Registry32 views so a 64-bit process reads the 32-bit
// WOW6432Node view correctly per webview2-runtime-detection-contract §4.
// All reads are read-only and require no elevation.
public sealed class RealRegistryProbe : IRegistryProbe
{
    public string? ReadString(string view, string subkey, string valueName)
    {
        RegistryHive hive;
        RegistryView regView;
        switch (view)
        {
            case "HKLM64": hive = RegistryHive.LocalMachine; regView = RegistryView.Registry64; break;
            case "HKLM32": hive = RegistryHive.LocalMachine; regView = RegistryView.Registry32; break;
            case "HKCU":   hive = RegistryHive.CurrentUser;  regView = RegistryView.Default;    break;
            default: return null;
        }
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, regView);
            using var k = baseKey.OpenSubKey(subkey, writable: false);
            return k?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }
}
