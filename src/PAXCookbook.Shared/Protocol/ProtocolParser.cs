namespace PAXCookbook.Shared.Protocol;

// Strict allowlist parser per paxcookbook-protocol-contract.md.
// Accepts ONLY:
//   paxcookbook://open
//   paxcookbook://open/
// Rejects: query, fragment, userinfo, port, alternate hosts, alternate
// paths, percent-encoding, control chars, inputs longer than 64 bytes.
public enum ProtocolRejectReason
{
    None,
    NullOrEmpty,
    TooLong,
    ControlCharacter,
    PercentEncoded,
    WrongScheme,
    WrongVerbHost,
    HasUserInfo,
    HasPort,
    HasQuery,
    HasFragment,
    DisallowedPath,
    UnparseableUri
}

public sealed record ProtocolParseResult(
    bool Accepted,
    string? RawInput,
    ProtocolRejectReason RejectReason)
{
    public static ProtocolParseResult Accept(string raw)
        => new(true, raw, ProtocolRejectReason.None);

    public static ProtocolParseResult Reject(string? raw, ProtocolRejectReason reason)
        => new(false, raw, reason);
}

public static class ProtocolParser
{
    public const int MaxInputLength = 64;

    public static ProtocolParseResult Parse(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return ProtocolParseResult.Reject(input, ProtocolRejectReason.NullOrEmpty);

        if (input.Length > MaxInputLength)
            return ProtocolParseResult.Reject(input, ProtocolRejectReason.TooLong);

        foreach (var ch in input)
        {
            if (char.IsControl(ch))
                return ProtocolParseResult.Reject(input, ProtocolRejectReason.ControlCharacter);
        }

        if (input.Contains('%'))
            return ProtocolParseResult.Reject(input, ProtocolRejectReason.PercentEncoded);

        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri))
            return ProtocolParseResult.Reject(input, ProtocolRejectReason.UnparseableUri);

        if (!string.Equals(uri.Scheme, ProductConstants.ProtocolScheme, StringComparison.Ordinal))
            return ProtocolParseResult.Reject(input, ProtocolRejectReason.WrongScheme);

        if (!string.Equals(uri.Host, ProductConstants.ProtocolOpenVerb, StringComparison.Ordinal))
            return ProtocolParseResult.Reject(input, ProtocolRejectReason.WrongVerbHost);

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return ProtocolParseResult.Reject(input, ProtocolRejectReason.HasUserInfo);

        // Uri.IsDefaultPort is true only when no explicit port was specified for
        // the scheme. For unregistered schemes Uri reports Port == -1 when
        // omitted and a non-negative value when explicit.
        if (uri.Port >= 0 && input.Contains(':' + uri.Port.ToString()))
            return ProtocolParseResult.Reject(input, ProtocolRejectReason.HasPort);

        if (!string.IsNullOrEmpty(uri.Query))
            return ProtocolParseResult.Reject(input, ProtocolRejectReason.HasQuery);

        if (!string.IsNullOrEmpty(uri.Fragment))
            return ProtocolParseResult.Reject(input, ProtocolRejectReason.HasFragment);

        // Allowed paths: "" (no path) or "/" only.
        var path = uri.AbsolutePath;
        if (path != string.Empty && path != "/")
            return ProtocolParseResult.Reject(input, ProtocolRejectReason.DisallowedPath);

        return ProtocolParseResult.Accept(input);
    }
}
