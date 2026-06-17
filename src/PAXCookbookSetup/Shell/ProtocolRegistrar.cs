using PAXCookbook.Shared;

namespace PAXCookbookSetup.Shell;

// Registers the paxcookbook:// URL protocol under HKCU\Software\Classes.
// Per Phase 8 contract: Setup OWNS HKCU registration only; the EXE owns
// parsing/validation of the URI string. No HKLM.
//
// Key shape (HKCU\Software\Classes\paxcookbook):
//   (default)        = "URL:PAX Cookbook Protocol"
//   URL Protocol     = "" (empty REG_SZ — required signal)
//   DefaultIcon\     = "<installRoot>\App\bin\PAXCookbook.exe,0"
//   shell\open\command\ = "<installRoot>\App\bin\PAX Cookbook.exe" protocol "%1"
public sealed class ProtocolRegistrar
{
    public const string RootSubKey = @"Software\Classes\paxcookbook";
    private readonly IRegistryWriter _registry;

    public ProtocolRegistrar(IRegistryWriter registry) { _registry = registry; }

    public ProtocolRegistrationResult Register(string installRoot)
    {
        var appExe = ShortcutCatalog.AppExePath(installRoot);
        // WDAC-safe + NO console window: wscript.exe runs the shipped launch.vbs,
        // which starts the signed dotnet.exe host hidden. DefaultIcon still
        // points at the apphost EXE (icon reads allowed).
        var command = DotNetLaunch.VbsLauncherCommand(installRoot, "protocol \"%1\"");

        _registry.SetString(RootSubKey, null, "URL:PAX Cookbook Protocol");
        _registry.SetString(RootSubKey, "URL Protocol", "");
        _registry.SetString(RootSubKey + @"\DefaultIcon", null, appExe + ",0");
        _registry.SetString(RootSubKey + @"\shell\open\command", null, command);

        return new ProtocolRegistrationResult(
            SubKey: RootSubKey,
            CommandLine: command,
            Scheme: ProductConstants.ProtocolScheme);
    }

    public bool IsRegistered()
        => _registry.SubKeyExists(RootSubKey + @"\shell\open\command");
}

public sealed record ProtocolRegistrationResult(string SubKey, string CommandLine, string Scheme);
