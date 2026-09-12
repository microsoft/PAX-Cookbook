// Bounded Work-account profile presentation — EXPERIMENTAL, gated.
//
// This entire file compiles ONLY under the EXPERIMENTAL_WAM constant. It defines
// the window-process-internal profile presentation, the initials deriver, the
// bounded factory, and the presentation sink seam.
//
// Containment doctrine (binding): the presentation is produced INSIDE the HWND-
// owning window process and is exposed ONLY to that process via the sink below.
// It carries NO token, UPN, tenant, object id, client id, account handle, claims,
// or URL. It is NEVER added to WamInteractiveResult in any way that reaches the
// daemon-bound NeutralWamResult; the daemon authorization IPC neither defines nor
// receives this type.

#if EXPERIMENTAL_WAM
using System;
using System.Globalization;

namespace PAXCookbook.App;

// The bounded state of the profile presentation.
internal enum WorkAccountProfileState
{
    // The accepted profile photo is available (bytes + content type).
    Photo = 0,

    // No accepted photo; the generic "Work account" identity is represented by
    // derived initials only.
    Initials = 1,
}

// The window-process-internal presentation the attached-window process may render.
// It carries a state, either the accepted image (bytes + content type) OR derived
// initials, and a generic "Work account" label flag. It carries no identity value
// of any kind beyond the derived initials.
internal sealed class WorkAccountProfilePresentation
{
    private WorkAccountProfilePresentation(
        WorkAccountProfileState state, byte[]? imageBytes, string? contentType, string initials)
    {
        State = state;
        ImageBytes = imageBytes;
        ContentType = contentType;
        Initials = initials;
    }

    internal WorkAccountProfileState State { get; }

    // Accepted image bytes — non-null ONLY when State == Photo.
    internal byte[]? ImageBytes { get; }

    // Exact accepted content type — non-null ONLY when State == Photo.
    internal string? ContentType { get; }

    // Derived initials (at most two uppercase letters, or the "WA" fallback).
    // Always populated so a photo failure degrades cleanly to initials.
    internal string Initials { get; }

    // The generic "Work account" label flag — carries no identity value.
    internal bool IsWorkAccountLabel => true;

    internal static WorkAccountProfilePresentation WithPhoto(
        byte[] imageBytes, string contentType, string initials) =>
        new(WorkAccountProfileState.Photo, imageBytes, contentType, NormalizeInitials(initials));

    internal static WorkAccountProfilePresentation WithInitials(string initials) =>
        new(WorkAccountProfileState.Initials, imageBytes: null, contentType: null, NormalizeInitials(initials));

    private static string NormalizeInitials(string? initials) =>
        string.IsNullOrEmpty(initials) ? WorkAccountInitialsDeriver.Fallback : initials;
}

// Derives at most two uppercase Unicode letters for the generic profile marker.
// Pure and testable. NEVER emits the source username/UPN or the part at/after
// '@': it strips everything from the first '@' onward, keeps only Unicode
// letters, and returns the fixed "WA" fallback when no usable letter remains.
internal static class WorkAccountInitialsDeriver
{
    internal const string Fallback = "WA";

    internal static string Derive(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return Fallback;
        }

        string local = source.Trim();

        int at = local.IndexOf('@');
        if (at >= 0)
        {
            local = local.Substring(0, at);
        }

        // Collect letter-runs (tokens) using Unicode letter classification. Any
        // digit, punctuation, control, or whitespace character is a separator and
        // is never emitted.
        string firstToken = string.Empty;
        string secondToken = string.Empty;
        var current = new System.Text.StringBuilder();

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            if (firstToken.Length == 0)
            {
                firstToken = current.ToString();
            }
            else if (secondToken.Length == 0)
            {
                secondToken = current.ToString();
            }

            current.Clear();
        }

        foreach (char c in local)
        {
            if (char.IsLetter(c))
            {
                if (firstToken.Length == 0 || secondToken.Length == 0)
                {
                    current.Append(c);
                }
            }
            else
            {
                Flush();
                if (firstToken.Length != 0 && secondToken.Length != 0)
                {
                    break;
                }
            }
        }

        Flush();

        string initials;
        if (firstToken.Length != 0 && secondToken.Length != 0)
        {
            // Two tokens: first letter of each.
            initials = string.Concat(FirstLetter(firstToken), FirstLetter(secondToken));
        }
        else if (firstToken.Length != 0)
        {
            // One token: up to its first two letters.
            initials = firstToken.Length >= 2 ? firstToken.Substring(0, 2) : firstToken.Substring(0, 1);
        }
        else
        {
            return Fallback;
        }

        initials = initials.ToUpper(CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(initials) ? Fallback : initials;
    }

    private static string FirstLetter(string token) => token.Substring(0, 1);
}

// Builds the bounded presentation from a photo fetch result and an initials
// source. Pure and never-throwing: an Available photo yields a Photo
// presentation; every other outcome (or a null/absent result) yields an Initials
// presentation. The initials source is derived and discarded — it is never
// retained on the presentation.
internal static class WorkAccountProfilePresentationFactory
{
    internal static WorkAccountProfilePresentation Build(
        WorkAccountPhotoFetchResult? photo, string? initialsSource)
    {
        string initials = WorkAccountInitialsDeriver.Derive(initialsSource);

        if (photo is not null &&
            photo.Outcome == WorkAccountPhotoOutcome.Available &&
            photo.ImageBytes is { Length: > 0 } bytes &&
            photo.ContentType is { Length: > 0 } contentType)
        {
            return WorkAccountProfilePresentation.WithPhoto(bytes, contentType, initials);
        }

        return WorkAccountProfilePresentation.WithInitials(initials);
    }
}

// The window-process-only presentation seam. An implementation is injected into
// the authenticator ONLY in the HWND-owning window process. The daemon never
// implements or receives this sink, so the presentation cannot cross the native
// authorization boundary.
internal interface IWorkAccountProfilePresentationSink
{
    void Publish(WorkAccountProfilePresentation presentation);

    // Clears any in-memory presentation currently held by the window process
    // (used by the native different-account action). Best-effort: it carries no
    // identity value and never blocks or reports account state.
    void Clear();
}
#endif
