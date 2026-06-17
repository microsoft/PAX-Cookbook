using PAXCookbook.Shared;
using PAXCookbookSetup.Shell;

namespace PAXCookbookSetup.Gui;

// Applies the wizard's "Start PAX Cookbook at login" choice. The PAX
// Cookbook install always registers the per-user HKCU Run value (the
// installer's default-on behaviour), so:
//
//   enabled  -> ensure the Run value is present (re-register; idempotent).
//   disabled -> remove the Run value by positive-ID (only our value, only
//               when it points under the install root).
//
// In BOTH cases we write the broker's "autostart-initialized" marker so the
// broker's own first-launch default-on logic never overrides the explicit
// choice the user just made in the wizard (the broker respects an explicit
// off only when this marker exists — see PAXCookbook.App.AutoStartSettingsModel).
//
// HKCU only (never HKLM); only our single named value; no secret. This stays
// inside the Brian-approved two-process / HKCU auto-start modification.
internal static class AutoStartChoice
{
    public static void Apply(string installRoot, bool enabled)
    {
        try
        {
            var registrar = new AutoStartRegistrar(new HkcuRegistryWriter());
            if (enabled) registrar.Register(installRoot);
            else registrar.Unregister(installRoot);
        }
        catch { /* best-effort: a registry hiccup must not fail the install */ }

        WriteInitializedMarker();
    }

    // Matches PAXCookbook.App.AutoStartSettingsModel.InitializedFlagPath():
    // %LOCALAPPDATA%\PAXCookbook\autostart-initialized. Always the broker's
    // canonical per-user location, independent of a custom install root.
    private static void WriteInitializedMarker()
    {
        try
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(baseDir, ProductConstants.InstallRootFolderName);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "autostart-initialized");
            if (!File.Exists(path))
                File.WriteAllText(path, DateTime.UtcNow.ToString("o"));
        }
        catch { /* best-effort marker */ }
    }
}
