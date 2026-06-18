namespace PAXCookbookSetup.Gui;

// Orchestrates the prerequisite installs for the wizard's Progress screen.
// All three prerequisites (.NET 8 Desktop Runtime, PowerShell 7, Python) are
// REQUIRED for PAX Cookbook to function. For each missing prerequisite, the
// coordinator runs the installer and — on a Failed or UserDeclined outcome —
// asks the caller (the wizard) whether to Retry or Exit Setup, looping on
// Retry. If the user chooses Exit, the coordinator marks the prerequisite as
// Cancelled and immediately returns (Setup must abort).
//
// Returns IsCancelled=true when any required prerequisite could not be
// satisfied because the user chose to exit. The wizard must NOT proceed with
// the PAX Cookbook install in this case.
public sealed class PrerequisiteCoordinator
{
    private readonly IReadOnlyList<IPrerequisiteInstaller> _installers;

    public PrerequisiteCoordinator(IReadOnlyList<IPrerequisiteInstaller> installers)
    {
        _installers = installers;
    }

    public PrerequisiteCoordinatorResult Run(
        IReadOnlyDictionary<PrerequisiteKind, bool> needed,
        string downloadDir,
        Action<string> progress,
        Func<PrerequisiteKind, string, RetryExitDecision> onError)
    {
        var results = new List<NamedPrerequisiteResult>();

        foreach (var installer in _installers)
        {
            var kind = installer.Kind;
            var name = DisplayName(kind);

            if (!needed.TryGetValue(kind, out var doInstall) || !doInstall)
            {
                // Prerequisite already satisfied — skip install step.
                results.Add(new NamedPrerequisiteResult(kind, name,
                    PrerequisiteInstallResult.AlreadyPresent($"{name} is already installed.")));
                continue;
            }

            // Retry loop: re-run the installer until it succeeds or the user
            // chooses Exit Setup on a failure/decline. The user controls the
            // loop; there is no artificial retry cap.
            while (true)
            {
                var result = installer.Install(downloadDir, progress);

                if (result.Outcome is PrerequisiteInstallOutcome.Failed
                                    or PrerequisiteInstallOutcome.UserDeclined)
                {
                    var decision = onError(kind, result.Message);
                    if (decision == RetryExitDecision.Retry) continue;
                    // User chose to exit — record cancellation and abort.
                    results.Add(new NamedPrerequisiteResult(kind, name,
                        PrerequisiteInstallResult.Cancelled($"{name} installation was cancelled.")));
                    return new PrerequisiteCoordinatorResult(results, IsCancelled: true);
                }

                results.Add(new NamedPrerequisiteResult(kind, name, result));
                break;
            }
        }

        return new PrerequisiteCoordinatorResult(results, IsCancelled: false);
    }

    private static string DisplayName(PrerequisiteKind kind) => kind switch
    {
        PrerequisiteKind.DotNet8DesktopRuntime => ".NET 8 Desktop Runtime",
        PrerequisiteKind.AspNetCoreRuntime => "ASP.NET Core 8 Runtime",
        PrerequisiteKind.PowerShell7 => "PowerShell 7",
        PrerequisiteKind.Python => "Python",
        _ => kind.ToString()
    };
}

// Result of running all prerequisite installers.
//
//   Results     - outcome for each prerequisite (may be partial if cancelled).
//   IsCancelled - true when a required prerequisite failed and the user chose
//                 to exit Setup. The wizard must NOT proceed with installing
//                 PAX Cookbook.
public sealed record PrerequisiteCoordinatorResult(
    IReadOnlyList<NamedPrerequisiteResult> Results,
    bool IsCancelled);
