using System.Windows.Forms;
using PAXCookbook.Shared.ExitCodes;

namespace PAXCookbookSetup.Gui;

// Hosts the visible in-place update wizard (the `apply-update` verb) on a
// dedicated STA thread, then returns its exit code. Invoked from Program.cs when
// the running app's broker launches `dotnet PAXCookbookSetup.dll apply-update`
// after the user clicks "Update now". The wizard downloads + installs the latest
// payload with visible progress and ends on a completion screen with
// Open / Close buttons.
internal static class UpdateGuiHost
{
    public static int Run(string installRoot, SetupLogger log)
    {
        int exitCode = SetupExitCodes.Ok;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using var form = new UpdateForm(installRoot, log);
                Application.Run(form);
                exitCode = form.ExitCode;
            }
            catch (Exception ex)
            {
                log.Write("update-gui-host-failed", "error",
                    new Dictionary<string, object?> { ["detail"] = ex.Message });
                exitCode = SetupExitCodes.InstallFailed;
            }
        })
        { Name = "PAXSetupUpdateUI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }
}
