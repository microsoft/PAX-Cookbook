using System.IO;
using System.Linq;
using PAXCookbook.Shared;
using PAXCookbookSetup.Shell;
using Xunit;

namespace PAXCookbookSetup.Tests;

// PAX recipe file associations (.paxlite + .pax) under HKCU\Software\Classes.
// All tests use the in-memory registry so no real HKCU writes happen.
public class FileAssociationTests
{
    private const string InstallRoot = @"C:\Users\Test\AppData\Local\PAXCookbook";

    private static string ExpectedExe =>
        ShortcutCatalog.AppExePath(InstallRoot);

    private static string LiteProgKey =>
        FileAssociationRegistrar.ProgIdSubKey(ProductConstants.PaxLiteProgId);

    private static string FullProgKey =>
        FileAssociationRegistrar.ProgIdSubKey(ProductConstants.PaxFullProgId);

    // ---- 1. Register creates both ProgIDs and both extension keys. ----
    [Fact]
    public void Register_CreatesBothProgIdsAndExtensionKeys()
    {
        var rg = new InMemoryRegistryWriter();
        var fa = new FileAssociationRegistrar(rg);

        var result = fa.Register(InstallRoot);

        Assert.Contains(ProductConstants.PaxLiteFileExtension, result.Extensions);
        Assert.Contains(ProductConstants.PaxFullFileExtension, result.Extensions);

        Assert.True(rg.SubKeyExists(LiteProgKey + @"\shell\open\command"));
        Assert.True(rg.SubKeyExists(FullProgKey + @"\shell\open\command"));

        // Extension keys point at our ProgIDs.
        Assert.Equal(ProductConstants.PaxLiteProgId,
            rg.GetString(FileAssociationRegistrar.ExtensionSubKey(
                ProductConstants.PaxLiteFileExtension), null));
        Assert.Equal(ProductConstants.PaxFullProgId,
            rg.GetString(FileAssociationRegistrar.ExtensionSubKey(
                ProductConstants.PaxFullFileExtension), null));
    }

    // ---- 2. open command is quoted exe + quoted %1. ----
    [Fact]
    public void Register_OpenCommandHasQuotedExeAndPercent1()
    {
        var rg = new InMemoryRegistryWriter();
        new FileAssociationRegistrar(rg).Register(InstallRoot);

        var cmd = rg.GetString(LiteProgKey + @"\shell\open\command", null);
        Assert.Equal($"\"{ExpectedExe}\" \"%1\"", cmd);
    }

    // ---- 3. DefaultIcon points at the app exe, index 0. ----
    [Fact]
    public void Register_DefaultIconPointsAtAppExe()
    {
        var rg = new InMemoryRegistryWriter();
        new FileAssociationRegistrar(rg).Register(InstallRoot);

        Assert.Equal(ExpectedExe + ",0",
            rg.GetString(LiteProgKey + @"\DefaultIcon", null));
        Assert.Equal(ExpectedExe + ",0",
            rg.GetString(FullProgKey + @"\DefaultIcon", null));
    }

    // ---- 4. ProgID default value is the friendly description. ----
    [Fact]
    public void Register_ProgIdDescriptionIsFriendlyName()
    {
        var rg = new InMemoryRegistryWriter();
        new FileAssociationRegistrar(rg).Register(InstallRoot);

        Assert.Equal(ProductConstants.PaxLiteDescription,
            rg.GetString(LiteProgKey, null));
        Assert.Equal(ProductConstants.PaxFullDescription,
            rg.GetString(FullProgKey, null));
    }

    // ---- 5. IsRegistered: false before, true after. ----
    [Fact]
    public void IsRegistered_FalseBefore_TrueAfter()
    {
        var rg = new InMemoryRegistryWriter();
        var fa = new FileAssociationRegistrar(rg);

        Assert.False(fa.IsRegistered());
        fa.Register(InstallRoot);
        Assert.True(fa.IsRegistered());
    }

    // ---- 6. ShellOperations.Install registers associations when wired. ----
    [Fact]
    public void ShellOperations_Install_RegistersFileAssociations()
    {
        var installRoot = Path.Combine(Path.GetTempPath(),
            "paxcookbook-fa-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installRoot);

        var sw = new InMemoryShortcutWriter();
        var rg = new InMemoryRegistryWriter();
        var ms = new ShortcutManifestStore();
        var startFolder = Path.Combine(installRoot, "StartMenu");
        var desktopFolder = Path.Combine(installRoot, "Desktop");
        var reg = new ShellRegistrar(sw, ms,
            startMenuFolderProvider: () => startFolder,
            desktopFolderProvider: () => desktopFolder);
        var prot = new ProtocolRegistrar(rg);
        var unin = new UninstallRegistrar(rg);
        var fa = new FileAssociationRegistrar(rg);
        var ops = new ShellOperations(reg, prot, unin, ms, fa);

        var r = ops.Install(installRoot, "1.2.3", createDesktopShortcut: false);

        Assert.True(r.FileAssociationsRegistered);
        Assert.True(ops.Inspect(installRoot).FileAssociationsRegistered);
        Assert.True(fa.IsRegistered());
    }

    // ---- 7. Without a registrar, Install does NOT touch associations. ----
    [Fact]
    public void ShellOperations_Install_WithoutRegistrar_NoAssociations()
    {
        var installRoot = Path.Combine(Path.GetTempPath(),
            "paxcookbook-fa-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(installRoot);

        var sw = new InMemoryShortcutWriter();
        var rg = new InMemoryRegistryWriter();
        var ms = new ShortcutManifestStore();
        var startFolder = Path.Combine(installRoot, "StartMenu");
        var desktopFolder = Path.Combine(installRoot, "Desktop");
        var reg = new ShellRegistrar(sw, ms,
            startMenuFolderProvider: () => startFolder,
            desktopFolderProvider: () => desktopFolder);
        var ops = new ShellOperations(reg,
            new ProtocolRegistrar(rg), new UninstallRegistrar(rg), ms);

        var r = ops.Install(installRoot, "1.2.3", createDesktopShortcut: false);

        Assert.False(r.FileAssociationsRegistered);
        Assert.False(ops.Inspect(installRoot).FileAssociationsRegistered);
        Assert.False(rg.SubKeyExists(LiteProgKey));
    }

    // ---- 8. ShellRemover removes OUR ProgIDs (positive-ID under root). ----
    [Fact]
    public void ShellRemover_RemovesOurProgIds_WhenCommandUnderInstallRoot()
    {
        var rg = new InMemoryRegistryWriter();
        new FileAssociationRegistrar(rg).Register(InstallRoot);

        var remover = new ShellRemover(
            new InMemoryShortcutWriter(), rg, new ShortcutManifestStore());
        var result = remover.Remove(InstallRoot);

        Assert.Equal(2, result.FileAssociationsRemoved);
        Assert.False(rg.SubKeyExists(LiteProgKey));
        Assert.False(rg.SubKeyExists(FullProgKey));
        Assert.False(rg.SubKeyExists(FileAssociationRegistrar.ExtensionSubKey(
            ProductConstants.PaxLiteFileExtension)));
    }

    // ---- 9. ShellRemover SKIPS a ProgID whose command is foreign. ----
    [Fact]
    public void ShellRemover_SkipsProgId_WhenCommandNotUnderInstallRoot()
    {
        var rg = new InMemoryRegistryWriter();
        new FileAssociationRegistrar(rg).Register(InstallRoot);

        var remover = new ShellRemover(
            new InMemoryShortcutWriter(), rg, new ShortcutManifestStore());
        // Remove against a DIFFERENT install root => our keys are foreign.
        var result = remover.Remove(@"C:\Some\Other\Place");

        Assert.Equal(0, result.FileAssociationsRemoved);
        Assert.Equal(2, result.FileAssociationsSkipped);
        Assert.True(rg.SubKeyExists(LiteProgKey));
        Assert.True(rg.SubKeyExists(FullProgKey));
    }

    // ---- 10. ShellRemover leaves an extension re-pointed by another app. ----
    [Fact]
    public void ShellRemover_LeavesExtension_WhenReassignedToOtherProgId()
    {
        var rg = new InMemoryRegistryWriter();
        new FileAssociationRegistrar(rg).Register(InstallRoot);

        // User/other app re-associates .paxlite to a different handler.
        var extKey = FileAssociationRegistrar.ExtensionSubKey(
            ProductConstants.PaxLiteFileExtension);
        rg.SetString(extKey, null, "SomeOtherApp.Handler");

        var remover = new ShellRemover(
            new InMemoryShortcutWriter(), rg, new ShortcutManifestStore());
        remover.Remove(InstallRoot);

        // ProgID (ours) removed, but the extension pointer is left intact.
        Assert.False(rg.SubKeyExists(LiteProgKey));
        Assert.True(rg.SubKeyExists(extKey));
        Assert.Equal("SomeOtherApp.Handler", rg.GetString(extKey, null));
    }
}
