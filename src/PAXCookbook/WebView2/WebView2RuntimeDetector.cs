namespace PAXCookbook.WebView2;

public enum WebView2RuntimeStatus
{
    Present,
    Missing,
    Unknown
}

public sealed record WebView2DetectionResult(
    WebView2RuntimeStatus Status,
    string? Pv,
    IReadOnlyList<string> Sources,
    bool PvParseFailed);

public interface IWebView2RuntimeDetector
{
    WebView2DetectionResult Detect();
}

// Probe all three documented locations for the Microsoft Edge WebView2
// Evergreen Runtime, in the order defined by
// webview2-runtime-detection-contract §3. Detection is read-only and
// never auto-installs the runtime.
public sealed class WebView2RuntimeDetector : IWebView2RuntimeDetector
{
    // F3017226-FE2A-4295-8BDF-00C3A9A7E4C5 is the documented WebView2
    // Evergreen Runtime EdgeUpdate Clients GUID. Do NOT change.
    public const string EdgeUpdateGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    // Per-machine HKLM key. On 64-bit Windows, HKLM\SOFTWARE is split into
    // the 64-bit view and the WOW6432Node 32-bit view; both must be probed
    // because the runtime installer can write to either depending on its
    // bitness (rare but observed).
    public static readonly string KeyHklm64 = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + EdgeUpdateGuid;
    public static readonly string KeyHklm32 = @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + EdgeUpdateGuid;
    // Per-user HKCU key. Per-user installs are real, admin-less installs
    // and MUST count as present; an HKLM-only probe was the v0
    // false-negative.
    public static readonly string KeyHkcu = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + EdgeUpdateGuid;

    private readonly IRegistryProbe _probe;

    public WebView2RuntimeDetector(IRegistryProbe probe)
    {
        _probe = probe;
    }

    public WebView2DetectionResult Detect()
    {
        var sources = new List<string>();
        string? firstPv = null;

        Try("HKLM64", KeyHklm64, @"HKLM64\SOFTWARE\Microsoft\EdgeUpdate\Clients\" + EdgeUpdateGuid);
        Try("HKLM32", KeyHklm32, @"HKLM32\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\" + EdgeUpdateGuid);
        Try("HKCU",   KeyHkcu,  @"HKCU\SOFTWARE\Microsoft\EdgeUpdate\Clients\" + EdgeUpdateGuid);

        if (sources.Count == 0)
        {
            return new WebView2DetectionResult(WebView2RuntimeStatus.Missing, null, sources, false);
        }
        var parseFailed = firstPv is not null && !System.Version.TryParse(firstPv, out _);
        return new WebView2DetectionResult(WebView2RuntimeStatus.Present, firstPv, sources, parseFailed);

        void Try(string view, string subkey, string canonical)
        {
            string? v;
            try { v = _probe.ReadString(view, subkey, "pv"); }
            catch { v = null; }
            if (string.IsNullOrWhiteSpace(v)) return;
            sources.Add(canonical);
            firstPv ??= v;
        }
    }
}

// Test/dev injectable detector that always returns Present. Used by
// existing Phase 4 tests so they keep passing without each one having
// to fake the registry.
public sealed class AlwaysPresentWebView2Detector : IWebView2RuntimeDetector
{
    public WebView2DetectionResult Detect() =>
        new(WebView2RuntimeStatus.Present, "0.0.0.0", Array.Empty<string>(), false);
}
