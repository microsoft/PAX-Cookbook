using System;

namespace PAXCookbook.App;

// Exact same-origin check for security-sensitive WebView2 host messages
// (Track 1 / T1-S2A native request-binding + restart-continuity repair).
//
// Before the window acts on a security-sensitive WebView host message (such as
// an experimental WAM request), the message's Source document origin must match
// the loopback application origin the window navigated to. The comparison is on
// the canonical origin triple — scheme, host, and effective port — using an
// ABSOLUTE-URI parse and exact equality. It deliberately uses NO suffix match
// and NO StartsWith, so an external site, a wrong port, or a malformed source
// can never satisfy it.
internal static class WebMessageOrigin
{
    internal static bool IsSameOrigin(string? source, string? expectedUrl)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(expectedUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? sourceUri) ||
            !Uri.TryCreate(expectedUrl, UriKind.Absolute, out Uri? expectedUri))
        {
            return false;
        }

        return string.Equals(sourceUri.Scheme, expectedUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(sourceUri.Host, expectedUri.Host, StringComparison.OrdinalIgnoreCase)
            && sourceUri.Port == expectedUri.Port;
    }
}
