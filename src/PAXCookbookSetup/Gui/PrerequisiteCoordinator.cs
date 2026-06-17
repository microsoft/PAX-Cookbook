namespace PAXCookbookSetup.Gui;

// Orchestrates the prerequisite installs for the wizard's Progress screen.
// For each prerequisite the user opted to install, it runs the installer and —
// on a Failed or UserDeclined outcome — asks the caller (the wizard) whether to
// Retry or Skip, looping on Retry. A prerequisite the user did not opt into is
// recorded as Skipped without running.
//
// A prerequisite failure NEVER aborts the sequence: PAX Cookbook must still
// install even when PowerShell 7 / Python could not be installed (the prompt's
// "show a warning, don't block"). The coordinator returns one result per
// prerequisite so the wizard can summarise on the Complete screen.
//
// All decision/looping logic is here (pure orchestration over the injected
// IPrerequisiteInstaller seam) so it is unit-tested with fakes.
public sealed class PrerequisiteCoordinator
{
    private readonly IReadOnlyList<IPrerequisiteInstaller> _installers;

    public PrerequisiteCoordinator(IReadOnlyList<IPrerequisiteInstaller> installers)
    {
        _installers = installers;
    }

    public IReadOnlyList<NamedPrerequisiteResult> Run(
        IReadOnlyDictionary<PrerequisiteKind, bool> wanted,
        string downloadDir,
        Action<string> progress,
        Func<PrerequisiteKind, string, RetrySkipDecision> onError)
    {
        var results = new List<NamedPrerequisiteResult>();

        foreach (var installer in _installers)
        {
            var kind = installer.Kind;
            var name = DisplayName(kind);

            if (!wanted.TryGetValue(kind, out var doInstall) || !doInstall)
            {
                results.Add(new NamedPrerequisiteResult(kind, name,
                    PrerequisiteInstallResult.Skipped($"{name} install was skipped.")));
                continue;
            }

            // Retry loop: re-run the installer until it succeeds or the user
            // chooses Skip on a failure/decline. The user controls the loop;
            // there is no artificial retry cap.
            while (true)
            {
                var result = installer.Install(downloadDir, progress);

                if (result.Outcome is PrerequisiteInstallOutcome.Failed
                                    or PrerequisiteInstallOutcome.UserDeclined)
                {
                    var decision = onError(kind, result.Message);
                    if (decision == RetrySkipDecision.Retry) continue;
                    // Skip: keep the (failed/declined) result and move on.
                }

                results.Add(new NamedPrerequisiteResult(kind, name, result));
                break;
            }
        }

        return results;
    }

    private static string DisplayName(PrerequisiteKind kind) => kind switch
    {
        PrerequisiteKind.DotNet8DesktopRuntime => ".NET 8 Desktop Runtime",
        PrerequisiteKind.PowerShell7 => "PowerShell 7",
        PrerequisiteKind.Python => "Python",
        _ => kind.ToString()
    };
}
