using System.Windows.Forms;
using PAXCookbook.Shared.ExitCodes;

namespace PAXCookbookSetup.Gui;

// Hosts the GUI uninstall (confirmation + progress) on a dedicated STA thread,
// then returns the chosen exit code. Invoked from Program.cs for an interactive
// uninstall; the scripted/silent path (`--quiet`) keeps the console CLI.
internal static class UninstallGuiHost
{
    public static int Run(string installRoot, ParsedArgs args, SetupLogger log)
    {
        int exitCode = SetupExitCodes.Ok;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new UninstallForm(installRoot, args, log);
                Application.Run(form);
                exitCode = form.ExitCode;
            }
            catch (Exception ex)
            {
                log.Write("uninstall-gui-host-failed", "error",
                    new Dictionary<string, object?> { ["detail"] = ex.Message });
                exitCode = SetupExitCodes.UninstallFailed;
            }
        })
        { Name = "PAXSetupUninstallUI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }
}
