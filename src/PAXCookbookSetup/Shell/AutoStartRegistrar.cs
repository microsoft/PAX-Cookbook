using System.IO;
using PAXCookbook.Shared;

namespace PAXCookbookSetup.Shell;

// Registers PAX Cookbook to start its background broker daemon at user
// logon (V2 two-process model). Writes a single named value under the
// per-user Run key:
//
//   HKCU\Software\Microsoft\Windows\CurrentVersion\Run
//     "PAX Cookbook" = "<installRoot>\App\bin\PAX Cookbook.exe" --headless
//                      --workspace "<installRoot>\Workspace" --approot "<installRoot>\App"
//
// The --headless launch hosts the in-process broker WITH a system-tray
// presence but NO window, so scheduled bakes fire in the background even
// when no window is open. Opening the app (Start Menu / Desktop shortcut)
// detects this already-running broker and attaches a window to it instead
// of starting a second broker.
//
// Scope: HKCU only (never HKLM), and only this ONE named value. The Run
// key is SHARED with other applications, so the key itself is never
// created or deleted — only OUR value is set (here) and removed (by
// ShellRemover, with positive-identification that the value points under
// the install root). The command carries the same --workspace/--approot
// as the primary launch shortcut so the daemon serves the canonical
// production workspace.
public sealed class AutoStartRegistrar
{
    public const string RootSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    // The Run value name is the label Windows shows in Task Manager's
    // Startup tab; it carries the product brand.
    public const string ValueName = "PAX Cookbook";

    private readonly IRegistryWriter _registry;

    public AutoStartRegistrar(IRegistryWriter registry) { _registry = registry; }

    public AutoStartRegistrationResult Register(string installRoot)
    {
        var installRootFull = Path.GetFullPath(installRoot);
        var appExe = Path.GetFullPath(ShortcutCatalog.AppExePath(installRootFull));
        var workspacePath = Path.Combine(installRootFull, ProductConstants.WorkspaceFolderName);
        var appRootPath = Path.Combine(installRootFull, ProductConstants.AppRootFolderName);

        var command =
            $"\"{appExe}\" --headless --workspace \"{workspacePath}\" --approot \"{appRootPath}\"";

        _registry.SetString(RootSubKey, ValueName, command);

        return new AutoStartRegistrationResult(
            SubKey: RootSubKey,
            ValueName: ValueName,
            CommandLine: command);
    }

    public bool IsRegistered()
        => !string.IsNullOrEmpty(_registry.GetString(RootSubKey, ValueName));

    // Removes OUR auto-start Run value, but only by positive identification:
    // the value is deleted only when its launch command's executable still
    // resolves under <installRoot>. A value pointing at a different install
    // (or another product) is left untouched. The shared Run KEY is never
    // created or deleted — only our single named value. Mirrors the
    // positive-ID discipline in ShellRemover so the wizard's "Start at login"
    // off path is symmetric with Register and uninstall.
    public AutoStartUnregistrationResult Unregister(string installRoot)
    {
        var current = _registry.GetString(RootSubKey, ValueName);
        if (string.IsNullOrEmpty(current))
            return new AutoStartUnregistrationResult(Removed: false, Skipped: false);

        if (!CommandPointsUnderInstallRoot(current!, installRoot))
            return new AutoStartUnregistrationResult(Removed: false, Skipped: true);

        bool removed = _registry.DeleteValue(RootSubKey, ValueName);
        return new AutoStartUnregistrationResult(Removed: removed, Skipped: false);
    }

    // True when the first (quoted or bare) token of the Run command is an
    // executable path that resolves under installRoot.
    private static bool CommandPointsUnderInstallRoot(string command, string installRoot)
    {
        try
        {
            var t = command.Trim();
            string exePath;
            if (t.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = t.IndexOf('"', 1);
                if (end < 0) return false;
                exePath = t.Substring(1, end - 1);
            }
            else
            {
                int sp = t.IndexOf(' ');
                exePath = sp < 0 ? t : t.Substring(0, sp);
            }
            var exeFull = Path.GetFullPath(exePath);
            var rootFull = Path.GetFullPath(installRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return exeFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}

public sealed record AutoStartRegistrationResult(
    string SubKey,
    string ValueName,
    string CommandLine);

public sealed record AutoStartUnregistrationResult(
    bool Removed,
    bool Skipped);