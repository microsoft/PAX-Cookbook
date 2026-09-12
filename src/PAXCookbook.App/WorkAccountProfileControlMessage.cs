#if EXPERIMENTAL_WAM
using System;
using System.Text.Json;

namespace PAXCookbook.App;

internal enum WorkAccountProfileControlAction
{
    None = 0,
    ReplayLatest,
    Clear,
}

// Presentation-only shell-to-native control. These exact one-field messages
// can replay or discard a bounded window-memory envelope; they cannot select a
// provider/account, authenticate, unlock, or mutate daemon authorization state.
internal static class WorkAccountProfileControlMessage
{
    internal const string ReadyType = "cookbook:work-account-profile-ready";
    internal const string ClearType = "cookbook:work-account-profile-clear";

    internal static bool TryParse(string? json, out WorkAccountProfileControlAction action)
    {
        action = WorkAccountProfileControlAction.None;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            using JsonElement.ObjectEnumerator fields = root.EnumerateObject();
            if (!fields.MoveNext() ||
                !string.Equals(fields.Current.Name, "type", StringComparison.Ordinal) ||
                fields.Current.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? type = fields.Current.Value.GetString();
            if (fields.MoveNext())
            {
                return false;
            }

            action = type switch
            {
                ReadyType => WorkAccountProfileControlAction.ReplayLatest,
                ClearType => WorkAccountProfileControlAction.Clear,
                _ => WorkAccountProfileControlAction.None,
            };
            return action != WorkAccountProfileControlAction.None;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryHandle(string? json, WorkAccountProfileWindowChannel channel)
    {
        if (channel is null || !TryParse(json, out WorkAccountProfileControlAction action))
        {
            return false;
        }

        if (action == WorkAccountProfileControlAction.ReplayLatest)
        {
            channel.ReplayLatest();
        }
        else
        {
            channel.Clear();
        }
        return true;
    }
}
#endif