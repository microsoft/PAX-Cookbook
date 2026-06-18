using System.Windows.Forms;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.Shared.Paths;

namespace PAXCookbookSetup.Gui;

// Entry point for the GUI installer (a double-click of the Setup EXE with
// no CLI arguments lands here). WinForms requires a single-threaded
// apartment for dialogs (folder browse, message boxes), so the message loop
// runs on a dedicated STA thread regardless of the host thread's apartment
// state, then the chosen exit code is returned to the process.
internal static class WizardLauncher
{
    public static int Run()
    {
        int exitCode = SetupExitCodes.Ok;
        var thread = new Thread(() => exitCode = RunWizard()) { Name = "PAXSetupWizardUI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static int RunWizard()
    {
        try
        {
            var installRoot = AppPaths.InstallRoot();
            var logsDir = Path.Combine(installRoot, "Logs", "Setup");
            using var log = new SetupLogger(logsDir);
            log.Write("wizard-start", fields: new Dictionary<string, object?>
            {
                ["installRoot"] = installRoot
            });

            var shellOps = ShellOperationsFactory.Build();
            PrereqLog.Begin();
            var detector = new PrerequisiteDetector(new SystemPrerequisiteProbe());

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var form = new SetupWizardForm(installRoot, log, shellOps, detector);
            Application.Run(form);

            log.Write("wizard-end", fields: new Dictionary<string, object?>
            {
                ["exitCode"] = form.ExitCode
            });
            return form.ExitCode;
        }
        catch (Exception ex)
        {
            try
            {
                MessageBox.Show(
                    "PAX Cookbook Setup could not start:\n\n" + ex.Message,
                    "PAX Cookbook Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* headless / no display — fall through to the exit code */ }
            return SetupExitCodes.GenericError;
        }
    }
}
