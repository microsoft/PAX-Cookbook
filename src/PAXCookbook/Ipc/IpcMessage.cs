namespace PAXCookbook.Ipc;

// IPC message envelope per paxcookbook-ipc-contract.md.
//
// Phase 4 ships the type + constants only. Wire protocol implementation
// lands when WebView2 single-instance coordination is required.
public sealed record IpcMessage(string Verb, IReadOnlyDictionary<string, string>? Fields);

public static class IpcConstants
{
    // Local\PAXCookbook.PrimaryInstance — see ProductConstants.PrimaryInstanceMutexName.
    public const string PrimaryInstanceMutexName = Shared.ProductConstants.PrimaryInstanceMutexName;

    // \\.\pipe\PAXCookbook.<sid>
    public static string PipeNameForSession(string sid)
        => Shared.ProductConstants.PipeNamePrefix + sid;

    public const string VerbOpen = "open";
    public const string VerbReopen = "reopen";
    public const string VerbStatus = "status";
}
