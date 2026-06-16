namespace PAXCookbook.WebView2;

public enum NavigationDecision
{
    Allow,
    Block
}

public sealed record NavigationEvaluation(NavigationDecision Decision, string Reason);

// Localhost-only navigation gate per webview2-host-contract §6.
// Allows http://localhost:<selectedPort>/... only. All other schemes
// (file/javascript/data), hosts (external HTTPS, 127.0.0.1, other),
// and wrong ports are blocked. Phase 6 cancels blocked navigation;
// external-browser redirection is deferred to a later phase.
public sealed class NavigationAllowlist
{
    private readonly int _port;

    public NavigationAllowlist(int selectedBrokerPort)
    {
        if (selectedBrokerPort <= 0 || selectedBrokerPort > 65535)
            throw new ArgumentOutOfRangeException(nameof(selectedBrokerPort));
        _port = selectedBrokerPort;
    }

    public int SelectedPort => _port;

    public NavigationEvaluation Evaluate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new NavigationEvaluation(NavigationDecision.Block, "empty");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
            return new NavigationEvaluation(NavigationDecision.Block, "not-absolute");
        // Reject non-http schemes outright: file/javascript/data/about/ftp/etc.
        if (u.Scheme != Uri.UriSchemeHttp)
            return new NavigationEvaluation(NavigationDecision.Block, "scheme-" + u.Scheme);
        // Require literal "localhost". 127.0.0.1 is rejected because the
        // SPA's WebAuthn origin assumptions depend on the "localhost"
        // host literal (security-contract).
        if (!string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            return new NavigationEvaluation(NavigationDecision.Block, "host-" + u.Host);
        if (u.Port != _port)
            return new NavigationEvaluation(NavigationDecision.Block, "port-" + u.Port);
        return new NavigationEvaluation(NavigationDecision.Allow, "ok");
    }
}
