using System;
using System.IO;

namespace PAXCookbook.App;

// First-launch auto-acquisition of the bundled PAX engine.
//
// On a fresh install the approved PAX engine ships inside the install tree at
// <appRoot>/resources/pax/PAX_Purview_Audit_Log_Processor.ps1 and its SHA-256
// is pinned in VERSION.json (paxScript.sha256). The per-user managed engine,
// however, lives under %LOCALAPPDATA%\PAXCookbook\Engine and is what the broker
// actually runs. Nothing copies the bundled engine to that managed location, so
// a fresh install would otherwise show the acquisition prompt even though the
// approved engine is sitting right there in the install tree.
//
// This module closes that gap: when the managed engine has not been acquired
// yet, it activates the bundled engine into the managed location through the
// SAME byte-preserving + triple-SHA-256-verifying activation pipeline the SPA
// acquisition flow uses, recording source = "automation" (a non-interactive,
// pre-approved acquisition). The result is a silent, ready-to-cook first launch
// with no prompt.
//
// It is intentionally conservative:
//   * a no-op once the engine is already acquired (idempotent),
//   * a no-op when the bundled engine is absent or its bytes do not match the
//     pinned approved hash (the normal acquisition prompt remains the fallback),
//   * best-effort: any failure leaves acquisition state untouched so the
//     existing prompt / local-file path still applies.
//
// The bundled file is never modified; the activator only reads it.
internal static class EngineBundleAutoAcquire
{
    internal static void TryAcquireFromBundle(
        VersionInfo version, string appRoot, string engineLocalAppDataBase)
    {
        try
        {
            // Already acquired and valid? Nothing to do.
            EngineAcquisitionResult current = EngineAcquisition.Resolve(version, engineLocalAppDataBase);
            if (current.IsAcquired)
            {
                return;
            }

            // The pinned approved hash must be a real 64-character hex value.
            string expected = (version.PaxSha256 ?? string.Empty).Trim().ToUpperInvariant();
            if (expected.Length != 64)
            {
                return;
            }

            // Locate the bundled engine inside the install tree.
            string relative = (version.PaxRelativePath ?? string.Empty)
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relative))
            {
                return;
            }

            string bundledPath = Path.GetFullPath(Path.Combine(appRoot, relative));
            if (!File.Exists(bundledPath))
            {
                return;
            }

            // Trust the bundled bytes only if they match the pinned approved hash.
            string bundledSha;
            try
            {
                bundledSha = Sha256Hex.OfFile(bundledPath);
            }
            catch
            {
                return;
            }
            if (!string.Equals(bundledSha, expected, StringComparison.Ordinal))
            {
                return;
            }

            string canonicalPath = EngineAcquisition.GetManagedEnginePath(engineLocalAppDataBase);
            string statePath = EngineAcquisition.GetInstallStatePath(engineLocalAppDataBase);

            // Copy bundled -> managed via the byte-preserving activator. Source
            // "automation": a non-interactive, pre-approved acquisition. The
            // activator re-verifies the SHA at every step and writes the
            // install-state success block (pending = false, activatedAtUtc set),
            // which is what makes the engine resolve as acquired.
            _ = ScriptActivator.Activate(new ActivationRequest
            {
                StagedFilePath = bundledPath,
                ExpectedSha256 = expected,
                Version = version.PaxVersion ?? string.Empty,
                CanonicalScriptPath = canonicalPath,
                Source = AcquisitionSources.Automation,
                StatePath = statePath,
            });
        }
        catch
        {
            // Best-effort: leave acquisition state unchanged so the normal
            // prompt / local-file fallback still applies.
        }
    }
}
