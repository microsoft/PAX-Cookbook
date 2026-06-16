namespace PAXCookbook.Commands;

// reopen — Phase 7: same flow as open but forwards "reopen" over IPC
// when a primary already owns the mutex.
public static class ReopenCommand
{
    public static int Run(CommandContext ctx)
    {
        ctx.Log.Write("App", "reopen-delegated-to-open", "info");
        return OpenCommand.RunWithForwardVerb(ctx, Ipc.IpcAllowlist.Reopen);
    }
}
