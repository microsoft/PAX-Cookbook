// Bounded native different-account intent receiver — EXPERIMENTAL, gated.
//
// This entire file compiles ONLY under the EXPERIMENTAL_WAM constant. It parses
// the shell-authored "use a different work account" intent and builds the bounded
// native acknowledgement. It is the native counterpart of the top-level shell
// profile menu's "Use a different work account" action.
//
// Containment doctrine (binding):
//   - The intent envelope is a CLOSED { type } shape with NO account-selection
//     field (no upn, tenant, account handle, homeAccountId, index, or any other
//     identity value). Any extra, missing, or renamed field is rejected.
//   - The origin of the intent is validated by the caller (WebViewShell) against
//     the exact top-level application origin BEFORE this receiver acts, exactly as
//     every other security-sensitive WebView host message is gated.
//   - The acknowledgement carries ONLY the fixed ack type and a bounded result
//     enum (cleared | failed). It carries no identifier, path, or exception
//     detail.

#if EXPERIMENTAL_WAM
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PAXCookbook.App;

internal static class WorkAccountDifferentAccountMessage
{
    // The shell-authored intent type: switch to a different work account.
    internal const string RequestType = "cookbook:work-account-different-account";

    // The native acknowledgement type posted back to the top-level document.
    internal const string AckType = "cookbook:work-account-different-account-ack";

    internal const string ResultCleared = "cleared";
    internal const string ResultFailed = "failed";

    // True ONLY for the exact closed intent envelope: a JSON object with exactly
    // one member named "type" whose value is RequestType. A wrong type, any extra
    // member (including any account-selection field), a missing type, or a non-
    // object payload is rejected. Pure and testable; never throws.
    internal static bool IsExactRequest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            int count = 0;
            bool typeOk = false;
            foreach (JsonProperty prop in root.EnumerateObject())
            {
                count++;
                if (count > 1)
                {
                    return false; // any second member (incl. account selection) rejects
                }

                if (!string.Equals(prop.Name, "type", StringComparison.Ordinal))
                {
                    return false;
                }

                typeOk = prop.Value.ValueKind == JsonValueKind.String &&
                         string.Equals(prop.Value.GetString(), RequestType, StringComparison.Ordinal);
            }

            return count == 1 && typeOk;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Builds the bounded acknowledgement: { type, result } with result in
    // { cleared | failed } only.
    internal static string BuildAck(bool cleared)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", AckType);
            w.WriteString("result", cleared ? ResultCleared : ResultFailed);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
#endif
