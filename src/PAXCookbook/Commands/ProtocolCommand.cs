using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.Shared.Protocol;

namespace PAXCookbook.Commands;

// protocol "%1" — validate via the strict allowlist parser, then dispatch
// to the open verb. NEVER forwards the raw URI to subprocesses or IPC.
public static class ProtocolCommand
{
    public static int Run(CommandContext ctx, string? rawUri)
    {
        if (string.IsNullOrEmpty(rawUri))
        {
            ctx.Stderr.WriteLine("protocol: missing argument");
            ctx.Log.Write("App", "protocol-missing-arg", "warn");
            return AppExitCodes.UsageError;
        }
        var r = ProtocolParser.Parse(rawUri);
        if (!r.Accepted)
        {
            // Log only the reject reason — never log the raw URI.
            ctx.Stderr.WriteLine("protocol: rejected (" + r.RejectReason + ")");
            ctx.Log.Write("App", "protocol-rejected", "warn", new Dictionary<string, object?>
            {
                ["reason"] = r.RejectReason.ToString()
            });
            return AppExitCodes.ProtocolRejected;
        }
        ctx.Log.Write("App", "protocol-accepted-dispatching-open", "info");
        return OpenCommand.Run(ctx);
    }
}
