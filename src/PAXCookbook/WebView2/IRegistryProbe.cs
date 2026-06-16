namespace PAXCookbook.WebView2;

// Abstraction over Windows registry reads so the WebView2 runtime
// detector can be unit-tested without touching the real machine
// registry. Production wires RealRegistryProbe; tests inject a fake
// dictionary-backed probe.
public interface IRegistryProbe
{
    // Returns the string value at (view, hive, subkey, valueName), or
    // null if the key/value is missing or unreadable. View is one of
    // "HKLM64", "HKLM32", "HKCU" per webview2-runtime-detection-contract.
    string? ReadString(string view, string subkey, string valueName);
}
