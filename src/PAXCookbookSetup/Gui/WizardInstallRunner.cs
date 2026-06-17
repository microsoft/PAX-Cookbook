using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbookSetup.Payload;
using PAXCookbookSetup.Shell;
using PAXCookbookSetup.Verbs;

namespace PAXCookbookSetup.Gui;

public sealed record WizardInstallResult(bool Success, int ExitCode, string? Error);

// Drives a PAX Cookbook install for the GUI wizard, reusing the SAME payload
// resolution + manifest verification + InstallVerb path as the CLI `install`
// verb (Program.RunPayloadVerb). The only differences are (a) it reports
// coarse progress through a callback for the Progress screen and (b) it
// returns a structured result instead of writing to the console.
//
// Prerequisite (PowerShell 7 / Python) installation is intentionally NOT
// performed here — that is wired into the wizard's Progress screen in a
// later slice. This runner only installs the PAX Cookbook application files
// and shell integration.
public static class WizardInstallRunner
{
    public static WizardInstallResult Run(
        string installRoot,
        string? payloadRootOverride,
        Action<string> progress,
        SetupLogger log,
        IShellOperations shellOps)
    {
        string? tempExtractRoot = null;
        try
        {
            IPayloadSourceResolver resolver;
            if (!string.IsNullOrEmpty(payloadRootOverride))
                resolver = new DirectoryPayloadSourceResolver(payloadRootOverride);
            else if (EmbeddedPayloadSourceResolver.HasEmbeddedPayload())
                resolver = new EmbeddedPayloadSourceResolver();
            else if (LocalCachePayloadSourceResolver.HasCache(installRoot))
                resolver = new LocalCachePayloadSourceResolver(installRoot);
            else
                return Fail(SetupExitCodes.UsageError,
                    "No installation payload is available. Run the distributable Setup EXE.");

            progress("Preparing installation files…");
            var src = resolver.Resolve();
            if (!src.Success || string.IsNullOrEmpty(src.PayloadRoot))
            {
                tempExtractRoot = src.TempExtractionRoot;
                return Fail(SetupExitCodes.InstallFailed, $"Could not prepare files: {src.Error}");
            }
            if (string.Equals(src.Origin, "embedded", StringComparison.Ordinal))
                tempExtractRoot = src.TempExtractionRoot;

            var payloadRoot = src.PayloadRoot!;
            var manifestPath = Path.Combine(payloadRoot, "manifest.json");
            if (!File.Exists(manifestPath))
                return Fail(SetupExitCodes.InstallFailed, "Installation payload is missing manifest.json.");

            Manifest m;
            try { m = ManifestSerializer.Deserialize(File.ReadAllText(manifestPath)); }
            catch (Exception ex) { return Fail(SetupExitCodes.InstallFailed, $"Payload manifest is invalid: {ex.Message}"); }

            if (string.Equals(src.Origin, "embedded", StringComparison.Ordinal) ||
                string.Equals(src.Origin, "local-cache", StringComparison.Ordinal))
            {
                var v = PayloadManifestVerifier.Verify(payloadRoot, m);
                if (!v.Ok)
                    return Fail(SetupExitCodes.InstallFailed,
                        "Installation payload failed verification: " + string.Join("; ", v.Errors));
            }

            progress("Installing application files…");
            var parsed = new ParsedArgs(
                Verb: "install", InstallRootOverride: installRoot, PayloadRoot: payloadRoot,
                Force: false, ReinstallSameVersion: false, AllowDowngrade: false,
                HandoffFromInstalled: false, HandoffFolder: null, DryRun: false,
                RemoveUserData: false, ConfirmRemoveUserData: false, Errors: new List<string>());

            int rc = InstallVerb.Run(parsed, m, payloadRoot, installRoot, log, shellOps: shellOps);
            if (rc != SetupExitCodes.Ok)
                return Fail(rc, $"Installation failed (exit code {rc}). See the Setup log for details.");

            progress("Finishing up…");
            return new WizardInstallResult(true, SetupExitCodes.Ok, null);
        }
        catch (Exception ex)
        {
            log.Write("wizard-install-exception", "error",
                new Dictionary<string, object?> { ["detail"] = ex.Message });
            return Fail(SetupExitCodes.GenericError, ex.Message);
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempExtractRoot))
                EmbeddedPayloadSourceResolver.TryCleanup(tempExtractRoot);
        }
    }

    private static WizardInstallResult Fail(int code, string message)
        => new(false, code, message);
}
