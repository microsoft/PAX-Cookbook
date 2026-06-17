using System.Collections.Generic;
using PAXCookbook.Shared;

namespace PAXCookbookSetup.Shell;

// Registers the .paxlite and .pax recipe file associations under
// HKCU\Software\Classes. Mirrors ProtocolRegistrar: Setup OWNS the HKCU
// registration only; the EXE owns parsing/validation of the file path.
// No HKLM, no admin.
//
// Two ProgIDs are created:
//   PAXCookbook.MiniRecipe.v1  <- .paxlite  (*.json.paxlite, Mini-Kitchen)
//   PAXCookbook.Recipe.v1      <- .pax      (*.json.pax, full Cookbook)
//
// ProgID key shape (HKCU\Software\Classes\<ProgId>):
//   (default)            = "<friendly description>"
//   DefaultIcon\         = "<installRoot>\App\bin\PAX Cookbook.exe,0"
//   shell\open\command\  = "<installRoot>\App\bin\PAX Cookbook.exe" "%1"
//
// Extension key shape (HKCU\Software\Classes\<.ext>):
//   (default)            = "<ProgId>"
public sealed class FileAssociationRegistrar
{
    public const string ClassesRoot = @"Software\Classes";

    public readonly record struct Association(
        string Extension, string ProgId, string Description);

    public static readonly IReadOnlyList<Association> Associations = new[]
    {
        new Association(
            ProductConstants.PaxLiteFileExtension,
            ProductConstants.PaxLiteProgId,
            ProductConstants.PaxLiteDescription),
        new Association(
            ProductConstants.PaxFullFileExtension,
            ProductConstants.PaxFullProgId,
            ProductConstants.PaxFullDescription),
    };

    private readonly IRegistryWriter _registry;

    public FileAssociationRegistrar(IRegistryWriter registry) { _registry = registry; }

    public static string ProgIdSubKey(string progId) => ClassesRoot + @"\" + progId;
    public static string ExtensionSubKey(string ext) => ClassesRoot + @"\" + ext;

    public FileAssociationRegistrationResult Register(string installRoot)
    {
        var appExe = ShortcutCatalog.AppExePath(installRoot);
        // WDAC-safe + NO console window: wscript.exe runs the shipped launch.vbs,
        // which starts the signed dotnet.exe host hidden. DefaultIcon still uses
        // the apphost EXE (icon reads are allowed).
        var command = DotNetLaunch.VbsLauncherCommand(installRoot, "\"%1\"");
        var registered = new List<string>();

        foreach (var a in Associations)
        {
            var progKey = ProgIdSubKey(a.ProgId);
            _registry.SetString(progKey, null, a.Description);
            _registry.SetString(progKey + @"\DefaultIcon", null, appExe + ",0");
            _registry.SetString(progKey + @"\shell\open\command", null, command);

            // Point the extension at our ProgID.
            _registry.SetString(ExtensionSubKey(a.Extension), null, a.ProgId);

            registered.Add(a.Extension);
        }

        return new FileAssociationRegistrationResult(
            Extensions: registered,
            CommandLine: command);
    }

    public bool IsRegistered()
    {
        foreach (var a in Associations)
        {
            if (!_registry.SubKeyExists(ProgIdSubKey(a.ProgId) + @"\shell\open\command"))
                return false;
        }
        return true;
    }
}

public sealed record FileAssociationRegistrationResult(
    IReadOnlyList<string> Extensions, string CommandLine);
