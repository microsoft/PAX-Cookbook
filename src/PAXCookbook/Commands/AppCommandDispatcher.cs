using PAXCookbook.Cli;
using PAXCookbook.Shared;
using PAXCookbook.Shared.ExitCodes;

namespace PAXCookbook.Commands;

// AppCommandDispatcher — single entry point used by Program.Main + tests.
//
// Tests construct a CommandContext with fakes and call Dispatch(parsed).
public static class AppCommandDispatcher
{
    public static int Dispatch(AppArgs parsed, CommandContext ctx)
    {
        switch (parsed.Verb)
        {
            case "version":
                return RunVersion(ctx);
            case "help":
            case "--help":
            case "-h":
            case "/?":
                return RunHelp(ctx);
            case "status":
                return StatusCommand.Run(ctx);
            case "open":
                return OpenCommand.Run(ctx);
            case "reopen":
                return ReopenCommand.Run(ctx);
            case "stop":
                return StopCommand.Run(ctx);
            case "support":
                return SupportCommand.Run(ctx);
            case "protocol":
                {
                    // --install-root is NOT permitted via the protocol verb.
                    if (ctx.InstallRootOverride is not null)
                    {
                        ctx.Stderr.WriteLine("protocol: --install-root is not accepted through the protocol path.");
                        ctx.Log.Write("App", "protocol-install-root-rejected", "warn");
                        return AppExitCodes.UsageError;
                    }
                    string? raw = parsed.Positional.Count > 0 ? parsed.Positional[0] : null;
                    return ProtocolCommand.Run(ctx, raw);
                }
            default:
                ctx.Stderr.WriteLine("unknown verb '" + parsed.Verb + "'");
                return AppExitCodes.UsageError;
        }
    }

    private static int RunVersion(CommandContext ctx)
    {
        ctx.Stdout.WriteLine(ProductConstants.ProductName + " " + ProductConstants.AppExeName);
        ctx.Stdout.WriteLine("AssemblyVersion " + typeof(AppCommandDispatcher).Assembly.GetName().Version);
        return AppExitCodes.Ok;
    }

    private static int RunHelp(CommandContext ctx)
    {
        ctx.Stdout.WriteLine(ProductConstants.AppExeName + " <verb> [args] [--install-root <path>]");
        ctx.Stdout.WriteLine();
        ctx.Stdout.WriteLine("Verbs:");
        ctx.Stdout.WriteLine("  open       Start (or reuse) the local broker; print URL/status. No browser.");
        ctx.Stdout.WriteLine("  reopen     Same as open in Phase 4 (WebView2 focus/reuse lands later).");
        ctx.Stdout.WriteLine("  stop       Stop the broker recorded in workspace.lock. Idempotent.");
        ctx.Stdout.WriteLine("  support    Not yet implemented in Phase 4.");
        ctx.Stdout.WriteLine("  status     Print JSON snapshot of install/broker state.");
        ctx.Stdout.WriteLine("  protocol   Validate a paxcookbook:// URI and dispatch to open.");
        ctx.Stdout.WriteLine("  version    Print product + assembly version.");
        ctx.Stdout.WriteLine("  help       Print this help.");
        return AppExitCodes.Ok;
    }
}
