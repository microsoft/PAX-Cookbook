// Bounded Work-account profile presentation channel — EXPERIMENTAL, gated.
//
// This entire file compiles ONLY under the EXPERIMENTAL_WAM constant. It is the
// window-process implementation of IWorkAccountProfilePresentationSink: it takes
// the window-process-internal WorkAccountProfilePresentation (produced INSIDE the
// HWND-owning window process by the authenticator) and posts a BOUNDED, closed-
// shape envelope to the TOP-LEVEL WebView2 document via
// CoreWebView2.PostWebMessageAsJson.
//
// Containment doctrine (binding):
//   - The envelope carries ONLY: the fixed message type; a bounded state enum
//     (photo | initials | none); when a photo, the exact accepted content type
//     ("image/jpeg" only) and the base64-encoded image bytes (bounded, encoded
//     size ceiling enforced BEFORE posting); when initials, at most two derived
//     characters; and a fixed generic "Work account" label. It NEVER carries a
//     URL, token, UPN, tenant id, object id, client id, account handle, claim,
//     scope, or any other identity value.
//   - The presentation is delivered ONLY to the top-level document of THIS
//     window process. It is never added to WamInteractiveResult and never crosses
//     to the daemon-bound NeutralWamResult; the daemon IPC neither defines nor
//     receives this type.
//   - The channel retains ONLY the latest already-bounded serialized envelope in
//     this window process until Clear or window exit. It retains no presentation
//     object, token, account value, raw claim, or unbounded image reference.
//   - A photo that fails its content-type or encoded-size guard degrades cleanly
//     to the initials envelope; a presentation delivery never blocks or alters
//     authorization.

#if EXPERIMENTAL_WAM
using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PAXCookbook.App;

// The window-process sink implementation. It posts the bounded envelope to the
// top-level document through the injected poster (CoreWebView2.PostWebMessageAsJson),
// marshalled onto the UI thread by the injected marshaller. Both are wired once
// the CoreWebView2 is ready. A Publish before then is retained for an explicit
// replay after the top-level document registers its receiver.
internal sealed class WorkAccountProfileWindowChannel : IWorkAccountProfilePresentationSink
{
    // The single closed message type the top-level shell receiver revalidates.
    internal const string MessageType = "cookbook:work-account-profile";

    // The bounded state values.
    internal const string StatePhoto = "photo";
    internal const string StateInitials = "initials";
    internal const string StateNone = "none";

    // The only accepted photo content type; a broad image/* is never emitted.
    internal const string AcceptedContentType = "image/jpeg";

    // The fixed generic label. Carries no identity value.
    internal const string LabelText = "Work account";

    // The encoded-size ceiling, derived from the raw 2 MiB fetch ceiling: a valid
    // photo is at most MaxPhotoBytes raw, whose base64 encoding is at most this
    // many characters. Enforced BEFORE posting so an over-ceiling encoding degrades
    // to initials rather than crossing to the shell.
    internal static readonly int MaxEncodedChars =
        ((WorkAccountProfilePhotoFetcher.MaxPhotoBytes + 2) / 3) * 4;

    private Action<string>? _poster;
    private Action<Action>? _marshal;
    private string? _latestEnvelope;
    private readonly object _gate = new();

    // Wire the top-level document poster and the UI-thread marshaller once the
    // CoreWebView2 is ready. Idempotent; a later call replaces the wiring.
    internal void SetPoster(Action<string> poster, Action<Action> marshal)
    {
        lock (_gate)
        {
            _poster = poster ?? throw new ArgumentNullException(nameof(poster));
            _marshal = marshal ?? throw new ArgumentNullException(nameof(marshal));
        }
    }

    public void Publish(WorkAccountProfilePresentation presentation)
    {
        if (presentation is null)
        {
            return;
        }

        string envelope = BuildEnvelope(presentation);
        lock (_gate)
        {
            _latestEnvelope = envelope;
            PostLocked(envelope);
        }
        // The presentation object and any raw image reference are dropped by the
        // caller. Only the bounded serialized envelope remains in window memory.
    }

    public void Clear()
    {
        lock (_gate)
        {
            _latestEnvelope = null;
            PostLocked(BuildClearEnvelope());
        }
    }

    internal void ReplayLatest()
    {
        lock (_gate)
        {
            if (_latestEnvelope is not null)
            {
                PostLocked(_latestEnvelope);
            }
        }
    }

    // Builds the bounded closed-shape envelope for a presentation. A Photo whose
    // content type is not exactly image/jpeg, or whose base64 encoding exceeds the
    // encoded ceiling, degrades to the initials envelope.
    internal static string BuildEnvelope(WorkAccountProfilePresentation presentation)
    {
        if (presentation.State == WorkAccountProfileState.Photo &&
            presentation.ImageBytes is { Length: > 0 } bytes &&
            string.Equals(presentation.ContentType, AcceptedContentType, StringComparison.Ordinal))
        {
            string encoded = Convert.ToBase64String(bytes);
            if (encoded.Length <= MaxEncodedChars)
            {
                return WritePhotoEnvelope(encoded);
            }
            // Over the encoded ceiling: degrade to initials.
        }

        return WriteInitialsEnvelope(presentation.Initials);
    }

    internal static string BuildClearEnvelope()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", MessageType);
            w.WriteString("state", StateNone);
            w.WriteString("label", LabelText);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string WritePhotoEnvelope(string imageBase64)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", MessageType);
            w.WriteString("state", StatePhoto);
            w.WriteString("contentType", AcceptedContentType);
            w.WriteString("imageBase64", imageBase64);
            w.WriteString("label", LabelText);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string WriteInitialsEnvelope(string initials)
    {
        // The presentation already normalizes initials to at most two characters
        // (or the "WA" fallback); re-clamp defensively so the envelope can never
        // exceed the bounded contract.
        string bounded = string.IsNullOrEmpty(initials)
            ? WorkAccountInitialsDeriver.Fallback
            : (initials.Length > 2 ? initials.Substring(0, 2) : initials);

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", MessageType);
            w.WriteString("state", StateInitials);
            w.WriteString("initials", bounded);
            w.WriteString("label", LabelText);
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // Called only while _gate is held so Publish, ReplayLatest, and Clear enqueue
    // their best-effort UI-thread posts in the same order as retained-state
    // transitions.
    private void PostLocked(string json)
    {
        Action<string>? poster = _poster;
        Action<Action>? marshal = _marshal;

        if (poster is null)
        {
            return; // Not wired yet: retain only; explicit replay posts later.
        }

        if (marshal is null)
        {
            try { poster(json); } catch { /* delivery is best-effort */ }
            return;
        }

        marshal(() =>
        {
            try { poster(json); } catch { /* delivery is best-effort */ }
        });
    }
}
#endif
