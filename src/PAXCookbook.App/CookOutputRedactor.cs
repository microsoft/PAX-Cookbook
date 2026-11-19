using System.Text.RegularExpressions;

namespace PAXCookbook.App;

internal sealed class CookOutputRedactor : IDisposable
{
    internal const string SpawnFailureMessage = "The Bake could not start or capture output safely.";
    private string[] _literals;
    private bool _disposed;
    private int _captureFailed;

    internal CookOutputRedactor(string? knownSecret)
    {
        _literals = string.IsNullOrEmpty(knownSecret) ? Array.Empty<string>() :
            new[] { knownSecret }.Concat(knownSecret.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                .Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length).ToArray();
    }

    internal bool CaptureFailed => Volatile.Read(ref _captureFailed) != 0;

    internal string Sanitize(string text) => Sanitize(text, string.Empty);

    private string Sanitize(string text, string delimiter)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string result = TelegramNotifier.TryParseDeviceCodePrompt(text, out _, out _)
            ? "[notify] device sign-in prompt detected; sign-in details omitted"
            : text;
        result = Regex.Replace(result, @"(?i)(\bBearer\s+)[^\s""'<>]+", "$1[REDACTED]",
            RegexOptions.None, TimeSpan.FromMilliseconds(250));
        result = Regex.Replace(result, @"\beyJ[0-9A-Za-z_-]{8,}\.[0-9A-Za-z_-]{8,}\.[0-9A-Za-z_-]{8,}\b", "[REDACTED]",
            RegexOptions.None, TimeSpan.FromMilliseconds(250));
        result = Regex.Replace(result, @"(?i)(\b(?:GRAPH_CLIENT_SECRET|client_secret|access_token|refresh_token)\b\s*[=:]\s*)(?:""[^""]*""|'[^']*'|[^\s,;]+)", "$1[REDACTED]",
            RegexOptions.None, TimeSpan.FromMilliseconds(250));
        result += delimiter;
        string replacement = _literals.Any(value => "[REDACTED]".Contains(value, StringComparison.Ordinal)) ? string.Empty : "[REDACTED]";
        foreach (string literal in _literals) { result = result.Replace(literal, replacement, StringComparison.Ordinal); }
        if (_literals.Length == 0) { return result; }
        var validText = new System.Text.StringBuilder(result.Length);
        for (int index = 0; index < result.Length; index++)
        {
            if (char.IsHighSurrogate(result[index]))
            {
                if (index + 1 < result.Length && char.IsLowSurrogate(result[index + 1]))
                {
                    validText.Append(result[index]);
                    validText.Append(result[++index]);
                }
            }
            else if (!char.IsLowSurrogate(result[index])) { validText.Append(result[index]); }
        }
        result = validText.ToString();
        bool removed;
        do
        {
            removed = false;
            foreach (string literal in _literals)
            {
                int position = result.IndexOf(literal, StringComparison.Ordinal);
                if (position >= 0)
                {
                    int end = position + literal.Length;
                    if (position > 0 && char.IsLowSurrogate(result[position]) && char.IsHighSurrogate(result[position - 1])) { position--; }
                    if (end < result.Length && char.IsHighSurrogate(result[end - 1]) && char.IsLowSurrogate(result[end])) { end++; }
                    result = result.Remove(position, end - position);
                    removed = true;
                }
                for (int length = Math.Min(literal.Length - 1, result.Length); length > 0; length--)
                {
                    if (!result.AsSpan().EndsWith(literal.AsSpan(0, length), StringComparison.Ordinal)) { continue; }
                    int end = result.Length - length;
                    if (end > 0 && char.IsHighSurrogate(result[end - 1]) && char.IsLowSurrogate(result[end])) { end--; }
                    result = result[..end];
                    removed = true;
                    break;
                }
            }
        } while (removed);
        return result;
    }

    internal void WriteLine(TextWriter writer, string line, string prefix = "")
    {
        try { writer.Write(Sanitize(prefix + line, writer.NewLine)); }
        catch
        {
            Interlocked.Exchange(ref _captureFailed, 1);
            throw new InvalidOperationException("output_capture_failed");
        }
    }

    public void Dispose()
    {
        Array.Clear(_literals, 0, _literals.Length);
        _literals = Array.Empty<string>();
        _disposed = true;
    }
}