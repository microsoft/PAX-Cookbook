using System.IO;
using PAXCookbook.Shared;

namespace PAXCookbookSetup.Shell;

// Writes the per-user Add/Remove Programs entry under
// HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\PAXCookbook.
//
// Fields written (per Phase 8 contract + Phase 12 Mode B failure repair):
//   DisplayName          = "PAX Cookbook"
//   DisplayVersion       = <appVersion>
//   Publisher            = "Microsoft"
//   InstallLocation      = <installRoot>                (absolute, expanded)
//   DisplayIcon          = <installRoot>\App\bin\PAXCookbook.exe,0
//   UninstallString      = "<dotnet.exe>" "<installRoot>\Setup\PAXCookbookSetup.dll" uninstall
//   QuietUninstallString = "<dotnet.exe>" "<installRoot>\Setup\PAXCookbookSetup.dll" uninstall --force
//   ModifyPath           = "<dotnet.exe>" "<installRoot>\Setup\PAXCookbookSetup.dll" repair
//   NoModify             = 0   (the "Modify" button runs ModifyPath -> repair)
//   NoRepair             = 0   (Repair Setup verb supports it)
//
// Uninstall runs the framework-dependent Setup DLL via the Microsoft-signed
// dotnet.exe host (WDAC-safe; the unsigned Setup apphost is never run from the
// install tree), launched HIDDEN through the Microsoft-signed wscript.exe on the
// shipped uninstall.vbs so no blank console window flashes during uninstall.
// All path values are normalized through Path.GetFullPath so the
// resulting strings are absolute, fully expanded, and contain no
// relative components. This is a defensive fix for the Phase 12 Mode B
// "Settings cannot find PAXCookbookSetup.exe" report: if the registry
// value is ever stored with a non-expanded or relative path,
// Settings.exe rejects it.
public sealed class UninstallRegistrar
{
    public const string RootSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PAXCookbook";

    public const string DisplayName = "PAX Cookbook";
    public const string Publisher = "Microsoft";

    private readonly IRegistryWriter _registry;

    public UninstallRegistrar(IRegistryWriter registry) { _registry = registry; }

    public UninstallRegistrationResult Register(string installRoot, string appVersion)
    {
        // Normalize installRoot to an absolute path; downstream values
        // are derived from it so the normalization flows through.
        var installRootFull = Path.GetFullPath(installRoot);
        var appExe = Path.GetFullPath(ShortcutCatalog.AppExePath(installRootFull));

        // Uninstall runs the framework-dependent Setup assembly directly through
        // the Microsoft-signed dotnet.exe host (no wscript / uninstall.vbs —
        // strict corporate WDAC blocks script hosts). Setup hides its own
        // console window at startup for the interactive uninstall, so no blank
        // terminal flashes — the only UI is the uninstall confirmation/progress
        // dialog. The DisplayIcon still points at the apphost EXE because icon
        // reads are allowed even when execution is not.
        var uninstallString = DotNetLaunch.SetupDllCommand(installRootFull, "uninstall");
        var quietUninstallString = DotNetLaunch.SetupDllCommand(installRootFull, "uninstall --force");
        // ModifyPath drives the Add/Remove Programs "Modify" button: it runs the
        // repair verb, which re-downloads the latest payload from GitHub, stops
        // the running app, overwrites the installed app files, and re-registers
        // shortcuts / ARP. NoModify=0 is what makes the button appear.
        var modifyString = DotNetLaunch.SetupDllCommand(installRootFull, "repair");
        var displayIcon = appExe + ",0";

        _registry.SetString(RootSubKey, "DisplayName", DisplayName);
        _registry.SetString(RootSubKey, "DisplayVersion", appVersion);
        _registry.SetString(RootSubKey, "Publisher", Publisher);
        _registry.SetString(RootSubKey, "InstallLocation", installRootFull);
        _registry.SetString(RootSubKey, "DisplayIcon", displayIcon);
        _registry.SetString(RootSubKey, "UninstallString", uninstallString);
        _registry.SetString(RootSubKey, "QuietUninstallString", quietUninstallString);
        _registry.SetString(RootSubKey, "ModifyPath", modifyString);
        _registry.SetDword (RootSubKey, "NoModify", 0);
        _registry.SetDword (RootSubKey, "NoRepair", 0);

        return new UninstallRegistrationResult(
            SubKey: RootSubKey,
            UninstallString: uninstallString,
            QuietUninstallString: quietUninstallString,
            DisplayIcon: displayIcon,
            DisplayName: DisplayName,
            DisplayVersion: appVersion,
            InstallLocation: installRootFull);
    }

    public bool IsRegistered()
        => _registry.SubKeyExists(RootSubKey);
}

public sealed record UninstallRegistrationResult(
    string SubKey,
    string UninstallString,
    string QuietUninstallString,
    string DisplayIcon,
    string DisplayName,
    string DisplayVersion,
    string InstallLocation);
