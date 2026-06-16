using PAXCookbook.Shared.ExitCodes;

namespace PAXCookbook.Commands;

// support — Phase 4 has no safe broker-only "support mode" path. The
// existing launcher\Start-PAXCookbookSupportMode.ps1 chains into
// launcher\Start-PAXCookbook.ps1 which opens the default browser, so
// PAXCookbook.exe MUST NOT call it from Phase 4. Return a clear
// not-implemented status; do not fake success.
public static class SupportCommand
{
    public static int Run(CommandContext ctx)
    {
        ctx.Stderr.WriteLine(
            "support: not yet implemented in Phase 4. Safe broker-only support mode " +
            "requires a non-browser-opening entry path that does not yet exist. Run " +
            "launcher\\Start-PAXCookbookSupportMode.ps1 directly if you need the " +
            "legacy visible-console behavior.");
        ctx.Log.Write("App", "support-not-implemented", "warn");
        return AppExitCodes.GenericError;
    }
}
