using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PAXCookbook.Output
{
    public static class CookOutputRedactor
    {
        public static void ValidateKnownSecret(string knownSecret)
        {
            if (knownSecret != null && knownSecret.Length > 16384)
            {
                throw new InvalidOperationException("output_redaction_unavailable");
            }
        }

        public static async Task PumpAsync(TextReader source, Stream destination, string knownSecret)
        {
            var patterns = new List<string>();
            var pending = new StringBuilder();
            var output = new StringBuilder();
            var emittedTail = new StringBuilder();
            var buffer = new char[4096];
            try
            {
                if (!string.IsNullOrEmpty(knownSecret))
                {
                    ValidateKnownSecret(knownSecret);
                    patterns.Add(knownSecret);
                    foreach (string fragment in knownSecret.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!patterns.Contains(fragment)) { patterns.Add(fragment); }
                    }
                    patterns.Sort((left, right) => right.Length.CompareTo(left.Length));
                }
                knownSecret = null;
                int tailLimit = patterns.Count == 0 ? 0 : patterns[0].Length - 1;
                using (var writer = new StreamWriter(destination, new UTF8Encoding(false), 4096, true))
                {
                    int count;
                    while ((count = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                        for (int index = 0; index < count; index++)
                        {
                            pending.Append(buffer[index]);
                            Emit(pending, output, emittedTail, patterns, tailLimit, false);
                        }
                        await writer.WriteAsync(output.ToString()).ConfigureAwait(false);
                        output.Clear();
                        Array.Clear(buffer, 0, buffer.Length);
                        await writer.FlushAsync().ConfigureAwait(false);
                    }
                    Emit(pending, output, emittedTail, patterns, tailLimit, true);
                    await writer.WriteAsync(output.ToString()).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                try { while (await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) > 0) { } }
                catch { }
                throw new InvalidOperationException("output_capture_failed");
            }
            finally
            {
                knownSecret = null;
                patterns.Clear();
                pending.Clear();
                output.Clear();
                emittedTail.Clear();
                Array.Clear(buffer, 0, buffer.Length);
            }
        }

        private static void Emit(StringBuilder pending, StringBuilder output, StringBuilder emittedTail, List<string> patterns, int tailLimit, bool final)
        {
            while (pending.Length > 0)
            {
                if (!final && pending.Length == 1 && char.IsHighSurrogate(pending[0])) { return; }
                string candidate = pending.ToString();
                string matched = patterns.Find(pattern => candidate.StartsWith(pattern, StringComparison.Ordinal));
                if (matched != null)
                {
                    int consumed = matched.Length;
                    if (char.IsHighSurrogate(candidate[consumed - 1]))
                    {
                        if (!final && consumed == candidate.Length) { return; }
                        if (consumed < candidate.Length && char.IsLowSurrogate(candidate[consumed])) { consumed++; }
                    }
                    if (!patterns.Exists(pattern => "[REDACTED]".Contains(pattern)))
                    {
                        AppendSafe("[REDACTED]", output, emittedTail, patterns, tailLimit);
                    }
                    pending.Remove(0, consumed);
                }
                else if (!final && patterns.Exists(pattern => pattern.StartsWith(candidate, StringComparison.Ordinal)))
                {
                    return;
                }
                else
                {
                    int consumed = pending.Length > 1 && char.IsHighSurrogate(pending[0]) && char.IsLowSurrogate(pending[1]) ? 2 : 1;
                    AppendSafe(pending.ToString(0, consumed), output, emittedTail, patterns, tailLimit);
                    pending.Remove(0, consumed);
                }
            }
        }

        private static void AppendSafe(string text, StringBuilder output, StringBuilder emittedTail, List<string> patterns, int tailLimit)
        {
            for (int index = 0; index < text.Length; index++)
            {
                int length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
                if (patterns.Count > 0 && length == 1 && char.IsSurrogate(text[index])) { continue; }
                string scalar = text.Substring(index, length);
                index += length - 1;
                string candidate = emittedTail.ToString() + scalar;
                if (patterns.Exists(pattern => candidate.IndexOf(pattern, StringComparison.Ordinal) >= 0)) { continue; }
                output.Append(scalar);
                emittedTail.Append(scalar);
                if (emittedTail.Length > tailLimit) { emittedTail.Remove(0, emittedTail.Length - tailLimit); }
            }
        }
    }
}