using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace PAXCookbook.App;

// Telegram notifications (CK-4) — the product notification feature.
//
// An opt-in, local-first relay that sends bake completion / failure summaries
// and a real-time Device Code sign-in code to the USER'S OWN Telegram bot. It is
// independent of any developer tooling: there is no embedded bot token here and
// no external bot is referenced. The bot token + chat id are configured by the
// user in Settings and stored in the per-user Windows Credential Manager vault.
//
// Constraint 1 reconciliation (local-first; no SaaS callbacks): a Telegram send
// is an outbound HTTPS call to api.telegram.org, which Brian explicitly
// sanctioned for this feature. The reconciliation is structural: the message
// payload is METADATA-ONLY. The builders below accept a recipe name (user
// chosen), a status, a duration, an output PATH and SIZE (a byte count, never a
// row count), and a failure classification. They NEVER receive — and so can
// never emit — Purview audit rows, fact.csv contents, tokens, or credentials.
// The feature is off by default and uses the user's own bot.
//
// Constraint 14 (secrets never leak): the bot token is SECRET-class. It lives in
// the WCM CredentialBlob, is read server-side only at send time, is never
// returned by any route (the projection carries a tokenSet boolean only), and is
// never written to cook.log, the cook sentinels, notify-status.json, a route
// response, or a report. The Device Code value relayed to the chat is the
// one-time sign-in code the USER needs to see; it is the purpose of the relay
// and is delivered ONLY to the configured chat — it is never written to cook.log.
//
// Reliability: every send path is wrapped so any failure (network down, bad
// token, timeout, throw) is caught and classified, and NEVER propagates to the
// caller. The bake's terminal state is always written first; a notification can
// never block or fail a bake.
internal static class TelegramNotifier
{
    // WCM storage convention (binding). Per-user vault, Generic, Persist
    // LocalMachine — exactly like Chef's Keys: NOT an HKLM write, NOT a service,
    // NOT shared across users, NO admin rights.
    internal const string SettingsTarget = "PAXCookbook:Settings:Telegram";

    private const string TelegramApiBase = "https://api.telegram.org";
    private const int SendTimeoutSeconds = 8;
    private const int MaxChatIdChars = 64;

    // ---------------------------------------------------------------------
    // Secret-free settings projection (what every GET / list surface returns).
    // ---------------------------------------------------------------------
    internal sealed record TelegramSettingsProjection(bool Enabled, string? ChatId, bool TokenSet);

    // Outcome of a wrapped send. Carries NO secret and NO message text — only a
    // coarse classification suitable for notify-status.json and diagnostics.
    internal enum NotifyOutcome
    {
        Disabled,        // notifications turned off
        Unconfigured,    // enabled but token and/or chat id missing
        Sent,            // Telegram accepted the message
        SendFailed,      // network / HTTP / timeout / throw — swallowed
    }

    // ---------------------------------------------------------------------
    // Injectable sender seam. The production implementation posts to
    // api.telegram.org; the smoke harness injects a fake (recording or throwing)
    // so the message-builder and swallow-all behavior are verifiable WITHOUT a
    // real network call.
    // ---------------------------------------------------------------------
    internal interface ITelegramSender
    {
        // Returns true on a 2xx Telegram response, false on a non-2xx. MAY throw
        // on a network / timeout failure; SendWrapped catches every throw.
        bool Send(string botToken, string chatId, string text);
    }

    internal sealed class HttpTelegramSender : ITelegramSender
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(SendTimeoutSeconds),
            };
            return client;
        }

        public bool Send(string botToken, string chatId, string text)
        {
            // The token appears ONLY in the request URL to api.telegram.org and
            // is never logged. sendMessage takes chat_id + text form fields.
            string url = TelegramApiBase + "/bot" + botToken + "/sendMessage";
            using var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("chat_id", chatId),
                new KeyValuePair<string, string>("text", text),
            });
            using HttpResponseMessage resp = Http.PostAsync(url, content).GetAwaiter().GetResult();
            return resp.StatusCode == HttpStatusCode.OK;
        }
    }

    // The default production sender. FinalizeCook and the stdout relay use this;
    // the CLI test seams inject their own fake.
    internal static readonly ITelegramSender DefaultSender = new HttpTelegramSender();

    // ---------------------------------------------------------------------
    // Pure message builders — METADATA ONLY (constraint 1). No tenant rows, no
    // file contents, no secret. These are directly unit-testable via the
    // --test-seam-telegram-build CLI seam.
    // ---------------------------------------------------------------------
    internal readonly record struct BakeNotificationMetadata(
        string? RecipeName,
        string Status,
        int? ExitCode,
        double DurationSeconds,
        string Trigger,
        string? OutputPath,
        long? OutputSizeBytes,
        string? FailureReason);

    internal static string BuildBakeCompletionMessage(BakeNotificationMetadata m)
    {
        var sb = new StringBuilder();
        sb.Append("PAX Cookbook — bake completed");
        if (IsScheduled(m.Trigger)) { sb.Append(" (scheduled run)"); }
        sb.Append('\n');
        sb.Append("Recipe: ").Append(SafeName(m.RecipeName)).Append('\n');
        sb.Append("Status: ").Append(m.Status).Append('\n');
        sb.Append("Duration: ").Append(FormatDuration(m.DurationSeconds)).Append('\n');
        if (!string.IsNullOrEmpty(m.OutputPath))
        {
            // Output LOCATION + SIZE only. The basename + size are metadata; the
            // file contents (tenant data) are NEVER read or included.
            sb.Append("Output: ").Append(FileNameOnly(m.OutputPath));
            if (m.OutputSizeBytes.HasValue)
            {
                sb.Append(" (").Append(FormatBytes(m.OutputSizeBytes.Value)).Append(')');
            }
            sb.Append('\n');
            sb.Append("Saved to: ").Append(m.OutputPath);
        }
        else
        {
            sb.Append("Output: no destination file was recorded");
        }
        return sb.ToString();
    }

    internal static string BuildBakeFailureMessage(BakeNotificationMetadata m)
    {
        var sb = new StringBuilder();
        sb.Append("PAX Cookbook — bake failed");
        if (IsScheduled(m.Trigger)) { sb.Append(" (scheduled run)"); }
        sb.Append('\n');
        sb.Append("Recipe: ").Append(SafeName(m.RecipeName)).Append('\n');
        sb.Append("Status: ").Append(m.Status).Append('\n');
        if (m.ExitCode.HasValue)
        {
            sb.Append("Exit code: ").Append(m.ExitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }
        sb.Append("Reason: ").Append(string.IsNullOrEmpty(m.FailureReason) ? "unknown" : m.FailureReason).Append('\n');
        sb.Append("Duration: ").Append(FormatDuration(m.DurationSeconds));
        return sb.ToString();
    }

    internal static string BuildDeviceCodeMessage(string url, string code)
    {
        return "PAX Cookbook — device sign-in required\n" +
               "Open: " + url + "\n" +
               "Enter code: " + code + "\n" +
               "This code is for the sign-in your bake started.";
    }

    internal static string BuildDeviceCodeExpiryMessage(string? recipeName)
    {
        return "PAX Cookbook — device sign-in expired\n" +
               "Recipe: " + SafeName(recipeName) + "\n" +
               "The sign-in window closed before authentication completed. Re-run the bake to get a new code.";
    }

    // ---------------------------------------------------------------------
    // Pure stdout parsers — extract the device-code prompt / expiry from a PAX
    // stdout line. Directly testable via --test-seam-telegram-parse-devicecode.
    // A non-matching line returns false and relays nothing (fails safe — never a
    // false relay).
    //
    // The MSAL device-code prompt looks like:
    //   "To sign in, use a web browser to open the page https://microsoft.com/devicelogin and enter the code XXXXXXXXX to authenticate."
    // ---------------------------------------------------------------------
    private static readonly Regex DeviceCodePattern = new(
        @"open the page\s+(?<url>https?://\S+?)\s+and enter the code\s+(?<code>[A-Za-z0-9-]{4,})\s+to authenticate",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A more permissive fallback: any line carrying a devicelogin URL and a code.
    private static readonly Regex DeviceCodeFallbackPattern = new(
        @"(?<url>https?://\S*devicelogin\S*).*?\bcode\s+(?<code>[A-Za-z0-9-]{4,})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DeviceCodeExpiryPattern = new(
        @"device\s+code.*(expired|timed out|has expired)|authentication.*(expired|timed out)|the code.*expired",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool TryParseDeviceCodePrompt(string? line, out string url, out string code)
    {
        url = string.Empty;
        code = string.Empty;
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        Match m = DeviceCodePattern.Match(line);
        if (!m.Success)
        {
            m = DeviceCodeFallbackPattern.Match(line);
        }
        if (!m.Success)
        {
            return false;
        }

        url = m.Groups["url"].Value.Trim().TrimEnd('.', ',');
        code = m.Groups["code"].Value.Trim();
        return url.Length > 0 && code.Length > 0;
    }

    internal static bool TryParseDeviceCodeExpiry(string? line)
    {
        return !string.IsNullOrEmpty(line) && DeviceCodeExpiryPattern.IsMatch(line);
    }

    // ---------------------------------------------------------------------
    // Settings store (WCM). UserName = JSON { enabled, chatId }; Blob = bot token.
    // ---------------------------------------------------------------------

    // Secret-free projection for the GET route and any read surface. Reads the
    // metadata + the HasSecret flag ONLY — the token blob is never copied out.
    internal static TelegramSettingsProjection LoadProjection()
    {
        WindowsCredentialStore.CredentialRecord? rec = WindowsCredentialStore.Read(SettingsTarget);
        if (rec is null)
        {
            return new TelegramSettingsProjection(false, null, false);
        }
        (bool enabled, string? chatId) = ParseSettingsMetadata(rec.UserName);
        return new TelegramSettingsProjection(enabled, chatId, rec.HasSecret);
    }

    // Pure projection builder used by both the GET route and the
    // --test-seam-telegram settings-projection seam. Given the stored metadata
    // JSON + the HasSecret flag, returns the secret-free wire object as a CLR
    // dictionary (serializable by both Results.Json and JsonModel). NEVER
    // includes the token.
    internal static Dictionary<string, object?> BuildSettingsResponse(string? metadataJson, bool tokenSet)
    {
        (bool enabled, string? chatId) = ParseSettingsMetadata(metadataJson);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["enabled"] = enabled,
            ["chatId"] = chatId,
            ["chatIdSet"] = !string.IsNullOrEmpty(chatId),
            ["tokenSet"] = tokenSet,
            ["provider"] = "telegram",
        };
    }

    private static (bool Enabled, string? ChatId) ParseSettingsMetadata(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
        {
            return (false, null);
        }
        if (JsonModel.Parse(metadataJson) is not Dictionary<string, object?> d)
        {
            return (false, null);
        }

        bool enabled = false;
        if (d.TryGetValue("enabled", out object? e))
        {
            enabled = e switch
            {
                bool b => b,
                string s => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        string? chatId = null;
        if (d.TryGetValue("chatId", out object? c))
        {
            string s = JsonModel.Str(c);
            if (!string.IsNullOrEmpty(s)) { chatId = s; }
        }
        return (enabled, chatId);
    }

    internal static string BuildSettingsMetadataJson(bool enabled, string? chatId)
    {
        var meta = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["enabled"] = enabled,
        };
        if (!string.IsNullOrEmpty(chatId)) { meta["chatId"] = chatId; }
        byte[] bytes = JsonModel.SerializeToUtf8Bytes(meta);
        return Encoding.UTF8.GetString(bytes);
    }

    // Persists settings. newTokenUtf16 is write-only: when it is non-null the
    // blob is replaced; when null the existing blob is preserved ("blank = keep",
    // mirroring Chef's Keys). The caller-supplied secret buffer and the preserved
    // buffer are zeroed before return.
    internal static void SaveSettings(bool enabled, string? chatId, byte[]? newTokenUtf16)
    {
        string metadataJson = BuildSettingsMetadataJson(enabled, chatId);

        byte[]? blob = newTokenUtf16;
        byte[]? preserved = null;
        try
        {
            if (blob is null)
            {
                preserved = WindowsCredentialStore.ReadSecretBytes(SettingsTarget);
                blob = preserved;
            }
            WindowsCredentialStore.Write(SettingsTarget, metadataJson, blob);
        }
        finally
        {
            if (preserved is not null) { Array.Clear(preserved, 0, preserved.Length); }
        }
    }

    internal static bool DeleteSettings() => WindowsCredentialStore.Delete(SettingsTarget);

    internal static int MaxChatIdLength => MaxChatIdChars;

    // ---------------------------------------------------------------------
    // The single swallow-all send primitive. EVERY high-level notify funnels
    // through here. It catches every exception the sender can throw and returns a
    // coarse outcome; it NEVER rethrows and NEVER logs the token or message.
    // ---------------------------------------------------------------------
    internal static NotifyOutcome SendWrapped(ITelegramSender sender, string botToken, string chatId, string text)
    {
        try
        {
            bool ok = sender.Send(botToken, chatId, text);
            return ok ? NotifyOutcome.Sent : NotifyOutcome.SendFailed;
        }
        catch
        {
            // Network down, bad token, timeout, or any throw from the sender —
            // swallowed. A notification can never block or fail a bake.
            return NotifyOutcome.SendFailed;
        }
    }

    // ---------------------------------------------------------------------
    // High-level notifications. Each loads the WCM settings, short-circuits when
    // disabled / unconfigured, builds a metadata-only message, sends through the
    // wrapped primitive, and zeroes the decoded token. None of these ever throw.
    // ---------------------------------------------------------------------
    internal static NotifyOutcome NotifyBakeTerminal(
        ITelegramSender sender, BakeNotificationMetadata metadata, bool success)
    {
        try
        {
            (bool enabled, string? chatId, byte[]? tokenBytes) = LoadForSend();
            try
            {
                if (!enabled) { return NotifyOutcome.Disabled; }
                if (string.IsNullOrEmpty(chatId) || tokenBytes is null || tokenBytes.Length == 0)
                {
                    return NotifyOutcome.Unconfigured;
                }

                string text = success
                    ? BuildBakeCompletionMessage(metadata)
                    : BuildBakeFailureMessage(metadata);
                string token = Encoding.Unicode.GetString(tokenBytes);
                return SendWrapped(sender, token, chatId!, text);
            }
            finally
            {
                if (tokenBytes is not null) { Array.Clear(tokenBytes, 0, tokenBytes.Length); }
            }
        }
        catch
        {
            return NotifyOutcome.SendFailed;
        }
    }

    internal static NotifyOutcome RelayDeviceCode(ITelegramSender sender, string url, string code)
    {
        try
        {
            (bool enabled, string? chatId, byte[]? tokenBytes) = LoadForSend();
            try
            {
                if (!enabled) { return NotifyOutcome.Disabled; }
                if (string.IsNullOrEmpty(chatId) || tokenBytes is null || tokenBytes.Length == 0)
                {
                    return NotifyOutcome.Unconfigured;
                }
                string text = BuildDeviceCodeMessage(url, code);
                string token = Encoding.Unicode.GetString(tokenBytes);
                return SendWrapped(sender, token, chatId!, text);
            }
            finally
            {
                if (tokenBytes is not null) { Array.Clear(tokenBytes, 0, tokenBytes.Length); }
            }
        }
        catch
        {
            return NotifyOutcome.SendFailed;
        }
    }

    internal static NotifyOutcome RelayDeviceCodeExpiry(ITelegramSender sender, string? recipeName)
    {
        try
        {
            (bool enabled, string? chatId, byte[]? tokenBytes) = LoadForSend();
            try
            {
                if (!enabled) { return NotifyOutcome.Disabled; }
                if (string.IsNullOrEmpty(chatId) || tokenBytes is null || tokenBytes.Length == 0)
                {
                    return NotifyOutcome.Unconfigured;
                }
                string text = BuildDeviceCodeExpiryMessage(recipeName);
                string token = Encoding.Unicode.GetString(tokenBytes);
                return SendWrapped(sender, token, chatId!, text);
            }
            finally
            {
                if (tokenBytes is not null) { Array.Clear(tokenBytes, 0, tokenBytes.Length); }
            }
        }
        catch
        {
            return NotifyOutcome.SendFailed;
        }
    }

    internal static string BuildTestMessage() =>
        "PAX Cookbook — test notification\n" +
        "If you can read this, notifications are configured correctly.";

    // Sends the "Send test notification" message. Reads the token from WCM
    // server-side, builds a fixed metadata-only message, and funnels through the
    // swallow-all primitive. Never throws; never returns the token.
    internal static NotifyOutcome NotifyTest(ITelegramSender sender)
    {
        try
        {
            (bool enabled, string? chatId, byte[]? tokenBytes) = LoadForSend();
            try
            {
                if (string.IsNullOrEmpty(chatId) || tokenBytes is null || tokenBytes.Length == 0)
                {
                    return NotifyOutcome.Unconfigured;
                }
                // The test route intentionally ignores the enabled flag: the user
                // is verifying the configuration before turning it on.
                _ = enabled;
                string token = Encoding.Unicode.GetString(tokenBytes);
                return SendWrapped(sender, token, chatId!, BuildTestMessage());
            }
            finally
            {
                if (tokenBytes is not null) { Array.Clear(tokenBytes, 0, tokenBytes.Length); }
            }
        }
        catch
        {
            return NotifyOutcome.SendFailed;
        }
    }

    // ---------------------------------------------------------------------
    // Chat-id auto-discovery. Reads the stored token server-side, calls Telegram
    // getUpdates, and returns the chat id of the most recent message the user
    // sent to their bot. The token is never returned. Runtime-only (a route
    // action); the smoke never exercises a real getUpdates call.
    // ---------------------------------------------------------------------
    internal enum ResolveChatIdOutcome { NoToken, NotFound, Found, Failed }

    internal readonly record struct ResolveChatIdResult(ResolveChatIdOutcome Outcome, string? ChatId);

    internal static ResolveChatIdResult ResolveChatIdViaGetUpdates()
    {
        byte[]? tokenBytes = null;
        try
        {
            WindowsCredentialStore.CredentialRecord? rec = WindowsCredentialStore.Read(SettingsTarget);
            if (rec is null || !rec.HasSecret)
            {
                return new ResolveChatIdResult(ResolveChatIdOutcome.NoToken, null);
            }
            tokenBytes = WindowsCredentialStore.ReadSecretBytes(SettingsTarget);
            if (tokenBytes is null || tokenBytes.Length == 0)
            {
                return new ResolveChatIdResult(ResolveChatIdOutcome.NoToken, null);
            }

            string token = Encoding.Unicode.GetString(tokenBytes);
            string url = TelegramApiBase + "/bot" + token + "/getUpdates";
            string body;
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(SendTimeoutSeconds) })
            using (HttpResponseMessage resp = client.GetAsync(url).GetAwaiter().GetResult())
            {
                if (resp.StatusCode != HttpStatusCode.OK)
                {
                    return new ResolveChatIdResult(ResolveChatIdOutcome.Failed, null);
                }
                body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }

            string? chatId = ExtractNewestChatId(body);
            return chatId is null
                ? new ResolveChatIdResult(ResolveChatIdOutcome.NotFound, null)
                : new ResolveChatIdResult(ResolveChatIdOutcome.Found, chatId);
        }
        catch
        {
            return new ResolveChatIdResult(ResolveChatIdOutcome.Failed, null);
        }
        finally
        {
            if (tokenBytes is not null) { Array.Clear(tokenBytes, 0, tokenBytes.Length); }
        }
    }

    // Walks a getUpdates response and returns the chat id of the newest update
    // that carries one. Pure (no network); the value is a chat id (a number),
    // never a secret.
    internal static string? ExtractNewestChatId(string? getUpdatesJson)
    {
        if (string.IsNullOrEmpty(getUpdatesJson))
        {
            return null;
        }
        if (JsonModel.Parse(getUpdatesJson) is not Dictionary<string, object?> root)
        {
            return null;
        }
        if (!(root.TryGetValue("result", out object? resultRaw) && resultRaw is List<object?> updates))
        {
            return null;
        }

        // Newest update last; walk backward to find the first that carries a chat id.
        for (int i = updates.Count - 1; i >= 0; i--)
        {
            if (updates[i] is not Dictionary<string, object?> update)
            {
                continue;
            }
            foreach (string carrier in new[] { "message", "edited_message", "channel_post", "my_chat_member" })
            {
                if (update.TryGetValue(carrier, out object? mRaw) &&
                    mRaw is Dictionary<string, object?> msg &&
                    msg.TryGetValue("chat", out object? cRaw) &&
                    cRaw is Dictionary<string, object?> chat &&
                    chat.TryGetValue("id", out object? idRaw))
                {
                    string id = JsonModel.Str(idRaw);
                    if (!string.IsNullOrEmpty(id))
                    {
                        return id;
                    }
                }
            }
        }
        return null;
    }

    // Reads enabled + chatId from metadata and the token bytes from the blob. The
    // caller MUST zero the returned token bytes. This is the only token-bearing
    // read in CK-4 and its result never reaches a response, log, or report.
    private static (bool Enabled, string? ChatId, byte[]? TokenBytes) LoadForSend()
    {
        WindowsCredentialStore.CredentialRecord? rec = WindowsCredentialStore.Read(SettingsTarget);
        if (rec is null)
        {
            return (false, null, null);
        }
        (bool enabled, string? chatId) = ParseSettingsMetadata(rec.UserName);
        byte[]? tokenBytes = WindowsCredentialStore.ReadSecretBytes(SettingsTarget);
        return (enabled, chatId, tokenBytes);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------
    private static bool IsScheduled(string? trigger) =>
        !string.IsNullOrEmpty(trigger) &&
        !string.Equals(trigger, "manual", StringComparison.OrdinalIgnoreCase);

    private static string SafeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "(unnamed recipe)" : name!;

    private static string FileNameOnly(string path)
    {
        try { return Path.GetFileName(path); }
        catch { return path; }
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 0) { seconds = 0; }
        if (seconds < 60)
        {
            return seconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s";
        }
        var ts = TimeSpan.FromSeconds(seconds);
        return ((int)ts.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture) + "m " +
               ts.Seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 0) { bytes = 0; }
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        string num = unit == 0
            ? value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        return num + " " + units[unit];
    }
}
