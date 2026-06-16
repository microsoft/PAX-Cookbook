namespace PAXCookbook.Shared.Security;

// Sanitizer stub. logging-contract.md and security-contract.md require
// that tokens, cookies, Hello credentials, HTTP bodies, raw URIs, query
// strings, user content, and off-allowlist paths never reach log files.
// Phase 2 ships a minimal redactor with a fixed blocklist; full pattern
// coverage and unit tests land in the phase that owns the logger.
public static class LogSanitizer
{
    private static readonly string[] BlockedFragments = new[]
    {
        "password", "secret", "token", "authorization", "cookie",
        "bearer", "apikey", "api_key", "client_secret"
    };

    public static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var lower = input.ToLowerInvariant();
        foreach (var b in BlockedFragments)
        {
            if (lower.Contains(b)) return "[REDACTED]";
        }
        return input;
    }
}
