using System.Text;
using System.Text.RegularExpressions;

namespace PAXCookbook.App;

// Settings → Notifications route handlers (CK-4).
//
// Four authenticated routes, all behind the same Bearer + CSRF + broker-lock
// gate as the Chef's Keys and recipe routes (enforced upstream in Program.cs):
//
//   GET  /api/v1/settings/notifications              secret-free projection
//   PUT  /api/v1/settings/notifications              save enabled + chatId + optional token
//   POST /api/v1/settings/notifications/test         send a test message (token read server-side)
//   POST /api/v1/settings/notifications/resolve-chat-id  auto-discover the chat id via getUpdates
//
// Constraint 14: the bot token is write-only. It is accepted on PUT, stored in
// the WCM blob, and NEVER returned by any route — GET / PUT responses carry only
// { enabled, chatId, chatIdSet, tokenSet, provider }. A blank/omitted token on
// PUT keeps the existing stored token (mirrors the Chef's Keys "blank = keep").
// The test + resolve routes read the token from WCM server-side and never echo
// it. All bodies are secret-free.
internal static class NotificationSettingsModel
{
    private static readonly HashSet<string> AllowedRequestKeys =
        new(StringComparer.OrdinalIgnoreCase) { "enabled", "chatId", "botToken", "provider" };

    // A Telegram chat id is an integer (negative for groups/supergroups) or an
    // @public_username. Bounded + structural; never a secret.
    private static readonly Regex ChatIdNumeric = new(@"^-?\d{1,20}$", RegexOptions.Compiled);
    private static readonly Regex ChatIdUsername = new(@"^@[A-Za-z0-9_]{3,64}$", RegexOptions.Compiled);

    // ---------------------------------------------------------------------
    // GET /api/v1/settings/notifications
    // ---------------------------------------------------------------------
    public static (int Status, object Body) Get()
    {
        WindowsCredentialStore.CredentialRecord? rec = WindowsCredentialStore.Read(TelegramNotifier.SettingsTarget);
        return (200, TelegramNotifier.BuildSettingsResponse(rec?.UserName, rec?.HasSecret ?? false));
    }

    // ---------------------------------------------------------------------
    // PUT /api/v1/settings/notifications
    // ---------------------------------------------------------------------
    public static (int Status, object Body) Put(object? body)
    {
        if (body is not Dictionary<string, object?> request)
        {
            return (400, new { error = "invalid_json" });
        }

        foreach (string key in request.Keys)
        {
            if (!AllowedRequestKeys.Contains(key))
            {
                return (400, Fail("unknown_field", key, "This field is not recognized for notification settings."));
            }
        }

        // provider — optional; when present must be telegram (the only provider
        // in this slice, named extensibly).
        string? provider = Str(request, "provider");
        if (provider is not null && !string.Equals(provider, "telegram", StringComparison.OrdinalIgnoreCase))
        {
            return (400, Fail("unsupported_provider", "provider", "Only the Telegram provider is supported in this version."));
        }

        // enabled — required boolean toggle.
        if (!request.TryGetValue("enabled", out object? enabledRaw) || enabledRaw is not bool enabled)
        {
            return (400, Fail("enabled_required", "enabled", "enabled must be provided as a boolean."));
        }

        // chatId — optional; bounded structural validation.
        string? chatId = Str(request, "chatId");
        if (chatId is not null)
        {
            if (chatId.Length > TelegramNotifier.MaxChatIdLength)
            {
                return (400, Fail("chat_id_too_long", "chatId", "The chat id is too long."));
            }
            if (!ChatIdNumeric.IsMatch(chatId) && !ChatIdUsername.IsMatch(chatId))
            {
                return (400, Fail("invalid_chat_id", "chatId",
                    "The chat id must be a number (from “Get my Chat ID”) or an @public_username."));
            }
        }

        // botToken — write-only. Non-empty replaces the stored token; blank /
        // omitted keeps the existing one. The value is never echoed back.
        string? botToken = Str(request, "botToken");
        byte[]? newTokenBytes = null;
        if (botToken is { Length: > 0 })
        {
            if (Encoding.Unicode.GetByteCount(botToken) > WindowsCredentialStore.MaxCredentialBlobBytes)
            {
                return (400, Fail("bot_token_too_long", "botToken", "The bot token is too large for secure storage."));
            }
            newTokenBytes = Encoding.Unicode.GetBytes(botToken);
        }

        // Enabling notifications requires a usable destination: a stored or
        // newly-supplied token AND a chat id (current or newly-supplied).
        if (enabled)
        {
            TelegramNotifier.TelegramSettingsProjection existing = TelegramNotifier.LoadProjection();
            bool willHaveToken = newTokenBytes is not null || existing.TokenSet;
            bool willHaveChatId = !string.IsNullOrEmpty(chatId) ||
                                  (chatId is null && !string.IsNullOrEmpty(existing.ChatId));
            if (!willHaveToken || !willHaveChatId)
            {
                if (newTokenBytes is not null) { Array.Clear(newTokenBytes, 0, newTokenBytes.Length); }
                return (400, Fail("incomplete_for_enable", "enabled",
                    "To turn on notifications, save a bot token and a chat id first."));
            }
        }

        // When chatId is omitted entirely, preserve the existing chat id rather
        // than clearing it (PUT is a partial save for chatId, like the token).
        string? chatIdToStore = chatId;
        if (chatId is null)
        {
            chatIdToStore = TelegramNotifier.LoadProjection().ChatId;
        }

        try
        {
            TelegramNotifier.SaveSettings(enabled, chatIdToStore, newTokenBytes);
        }
        catch
        {
            if (newTokenBytes is not null) { Array.Clear(newTokenBytes, 0, newTokenBytes.Length); }
            return (500, new { error = "settings_write_failed", message = "The notification settings could not be saved." });
        }
        finally
        {
            if (newTokenBytes is not null) { Array.Clear(newTokenBytes, 0, newTokenBytes.Length); }
        }

        TelegramNotifier.TelegramSettingsProjection saved = TelegramNotifier.LoadProjection();
        return (200, new
        {
            enabled = saved.Enabled,
            chatId = saved.ChatId,
            chatIdSet = !string.IsNullOrEmpty(saved.ChatId),
            tokenSet = saved.TokenSet,
            provider = "telegram",
            saved = true,
        });
    }

    // ---------------------------------------------------------------------
    // POST /api/v1/settings/notifications/test — send a real test message at
    // runtime. The token is read from WCM server-side; the response is secret-free.
    // ---------------------------------------------------------------------
    public static (int Status, object Body) Test()
    {
        TelegramNotifier.NotifyOutcome outcome = TelegramNotifier.NotifyTest(TelegramNotifier.DefaultSender);
        switch (outcome)
        {
            case TelegramNotifier.NotifyOutcome.Sent:
                return (200, new { ok = true, status = "sent", message = "A test notification was sent to your chat." });
            case TelegramNotifier.NotifyOutcome.Unconfigured:
                return (400, new { ok = false, status = "unconfigured", message = "Save a bot token and a chat id before sending a test." });
            default:
                return (502, new { ok = false, status = "send_failed", message = "The test message could not be delivered. Check the bot token and chat id." });
        }
    }

    // ---------------------------------------------------------------------
    // POST /api/v1/settings/notifications/resolve-chat-id — auto-discover the
    // chat id from the bot's recent updates. The token is read server-side and
    // never returned; only the discovered chat id (a number) is.
    // ---------------------------------------------------------------------
    public static (int Status, object Body) ResolveChatId()
    {
        TelegramNotifier.ResolveChatIdResult result = TelegramNotifier.ResolveChatIdViaGetUpdates();
        switch (result.Outcome)
        {
            case TelegramNotifier.ResolveChatIdOutcome.Found:
                return (200, new { found = true, chatId = result.ChatId });
            case TelegramNotifier.ResolveChatIdOutcome.NotFound:
                return (200, new
                {
                    found = false,
                    message = "No recent message was found. Open your bot in Telegram, send it any message, then try again.",
                });
            case TelegramNotifier.ResolveChatIdOutcome.NoToken:
                return (400, new { found = false, error = "no_token", message = "Save your bot token first, then use “Get my Chat ID”." });
            default:
                return (502, new { found = false, error = "telegram_unreachable", message = "Telegram could not be reached. Check the bot token and your connection." });
        }
    }

    // ---------------------------------------------------------------------
    // Helpers — validation errors reference field NAMES only, never values, so a
    // submitted token can never appear in a response (constraint 14).
    // ---------------------------------------------------------------------
    private static object Fail(string reason, string field, string message) =>
        new { error = "validation_failed", reason, field, message };

    private static string? Str(Dictionary<string, object?> d, string key)
    {
        if (d.TryGetValue(key, out object? v) && v is string s)
        {
            string t = s.Trim();
            return t.Length > 0 ? t : null;
        }
        return null;
    }
}
