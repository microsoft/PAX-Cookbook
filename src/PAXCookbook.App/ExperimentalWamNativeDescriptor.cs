using System;

namespace PAXCookbook.App;

// Daemon-authored native descriptor for the two-stage experimental WAM pipe
// protocol (Track 1 / T1-S2A native request-binding + restart-continuity repair).
//
// Stage 1 of the native pipe is a NON-CONSUMING lookup: the HWND-owning window
// sends only the opaque requestId, and the daemon returns this minimal, fully
// daemon-authoritative descriptor. The window derives its behavior SOLELY from
// this descriptor; the renderer never authors any field here.
//
// The descriptor is deliberately minimal: requestId and the authoritative
// purpose. It carries NO recipe id, lock generation, challenge, token, raw
// claim, account handle, or granted-scope array. The challenge stays entirely
// daemon-owned in the pending request.
internal enum WamDescriptorReason
{
    Found = 0,
    NotConfigured = 1,
    Unknown = 2,
    Expired = 3,
    Terminal = 4,
    Consumed = 5,
    Malformed = 7,
}

internal sealed class WamNativeDescriptor
{
    private WamNativeDescriptor(
        bool found,
        WamDescriptorReason reason,
        WamAuthPurpose purpose)
    {
        Found = found;
        Reason = reason;
        Purpose = purpose;
    }

    // True only when the daemon resolved a live, pending, unconsumed, unexpired
    // request for the supplied requestId. Every other state is a fail-closed
    // NotFound with a bounded reason.
    internal bool Found { get; }

    internal WamDescriptorReason Reason { get; }

    // The authoritative purpose. Session unlock is the only concern.
    internal WamAuthPurpose Purpose { get; }

    internal static WamNativeDescriptor NotFound(WamDescriptorReason reason) =>
        new(false, reason, WamAuthPurpose.SessionUnlock);

    internal static WamNativeDescriptor ForSession() =>
        new(true, WamDescriptorReason.Found, WamAuthPurpose.SessionUnlock);
}
