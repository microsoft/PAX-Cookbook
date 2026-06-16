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
}

public sealed record AutoStartRegistrationResult(
    string SubKey,
    string ValueName,
    string CommandLine);
