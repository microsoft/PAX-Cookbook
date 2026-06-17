using System.IO;
using PAXCookbookSetup.Shell;
using Xunit;

namespace PAXCookbookSetup.Tests;

// V2 two-process model — HKCU auto-start (Run value) register + remove.
// All tests use InMemoryRegistryWriter so no real HKCU writes happen.
//
// Contract under test:
//   - AutoStartRegistrar writes ONE named value "PAX Cookbook" under the
//     SHARED Run key, carrying "<wscript.exe>" "<installRoot>\App\bin\launch.vbs"
//     --headless --workspace <ws> --approot <app> (hidden launcher, WDAC-safe).
//   - ShellRemover removes that value ONLY when it points under installRoot
//     (positive-ID); a value pointing elsewhere is left intact.
//   - ShellOperations.Install wires the registrar through so the install
//     result reports AutoStartRegistered.
public class AutoStartTests
{
    private const string AppVersion = "1.2.3";
    private const string InstallRoot = @"C:\Users\Test\AppData\Local\PAXCookbook";

    private static string FreshInstallRoot()
    {
        var p = Path.Combine(Path.GetTempPath(),
            "paxcookbook-autostart-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p);
        return p;
    }

    // ---- 1. Register writes the headless launch command under Run. ----
    [Fact]
    public void Register_WritesHeadlessRunValue_UnderInstallRoot()
    {
        var rg = new InMemoryRegistryWriter();
        var reg = new AutoStartRegistrar(rg);

        var result = reg.Register(InstallRoot);

        var cmd = rg.GetString(AutoStartRegistrar.RootSubKey, AutoStartRegistrar.ValueName);
        Assert.NotNull(cmd);
        Assert.Equal(result.CommandLine, cmd);
        Assert.Contains("--headless", cmd);
        // No console window: launches via wscript.exe + the hidden launch.vbs
        // (which starts the signed dotnet host), NOT dotnet/the apphost directly.
        Assert.Contains("wscript.exe", cmd);
        Assert.Contains(@"App\bin\launch.vbs", cmd);
        Assert.DoesNotContain(@"App\bin\PAX Cookbook.exe", cmd);
        Assert.Contains(@"--workspace """ + Path.Combine(InstallRoot, "Workspace") + @"""", cmd);
        Assert.Contains(@"--approot """ + Path.Combine(InstallRoot, "App") + @"""", cmd);
        // The Run value name is the product brand (Task Manager Startup label).
        Assert.Equal("PAX Cookbook", AutoStartRegistrar.ValueName);
        // The Run KEY is the shared per-user Run key — never an HKLM path.
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", AutoStartRegistrar.RootSubKey);
    }

    // ---- 2. IsRegistered reflects the presence of the Run value. ----
    [Fact]
    public void IsRegistered_TrueOnlyAfterRegister()
    {
        var rg = new InMemoryRegistryWriter();
        var reg = new AutoStartRegistrar(rg);

        Assert.False(reg.IsRegistered());
        reg.Register(InstallRoot);
        Assert.True(reg.IsRegistered());
    }

    // ---- 3. ShellRemover removes the Run value (positive-ID match). ----
    [Fact]
    public void Remove_DeletesRunValue_WhenPointingUnderInstallRoot()
    {
        var installRoot = FreshInstallRoot();
        var rg = new InMemoryRegistryWriter();
        new AutoStartRegistrar(rg).Register(installRoot);
        Assert.NotNull(rg.GetString(AutoStartRegistrar.RootSubKey, AutoStartRegistrar.ValueName));

        var remover = new ShellRemover(new InMemoryShortcutWriter(), rg, new ShortcutManifestStore());
        var result = remover.Remove(installRoot);

        Assert.True(result.AutoStartRemoved);
        Assert.False(result.AutoStartSkipped);
        Assert.Null(rg.GetString(AutoStartRegistrar.RootSubKey, AutoStartRegistrar.ValueName));
    }

    // ---- 4. ShellRemover LEAVES a Run value pointing elsewhere. ----
    [Fact]
    public void Remove_SkipsRunValue_WhenPointingOutsideInstallRoot()
    {
        var installRoot = FreshInstallRoot();
        var rg = new InMemoryRegistryWriter();
        // A Run value owned by a DIFFERENT installation.
        var foreign = @"""C:\Other\App\bin\PAX Cookbook.exe"" --headless";
        rg.SetString(AutoStartRegistrar.RootSubKey, AutoStartRegistrar.ValueName, foreign);

        var remover = new ShellRemover(new InMemoryShortcutWriter(), rg, new ShortcutManifestStore());
        var result = remover.Remove(installRoot);

        Assert.False(result.AutoStartRemoved);
        Assert.True(result.AutoStartSkipped);
        Assert.Equal(foreign, rg.GetString(AutoStartRegistrar.RootSubKey, AutoStartRegistrar.ValueName));
    }

    // ---- 5. No Run value at all is a clean no-op. ----
    [Fact]
    public void Remove_NoRunValue_IsNoOp()
    {
        var installRoot = FreshInstallRoot();
        var rg = new InMemoryRegistryWriter();

        var remover = new ShellRemover(new InMemoryShortcutWriter(), rg, new ShortcutManifestStore());
        var result = remover.Remove(installRoot);

        Assert.False(result.AutoStartRemoved);
        Assert.False(result.AutoStartSkipped);
    }

    // ---- 6. ShellOperations.Install wires the registrar through. ----
    [Fact]
    public void Install_WithAutoStartRegistrar_ReportsRegisteredAndWritesValue()
    {
        var installRoot = FreshInstallRoot();
        var sc = new InMemoryShortcutWriter();
        var rg = new InMemoryRegistryWriter();
        var ms = new ShortcutManifestStore();
        var startFolder = Path.Combine(installRoot, "StartMenu");
        var desktopFolder = Path.Combine(installRoot, "Desktop");
        var shortcuts = new ShellRegistrar(sc, ms,
            startMenuFolderProvider: () => startFolder,
            desktopFolderProvider: () => desktopFolder);
        var protocol = new ProtocolRegistrar(rg);
        var uninstall = new UninstallRegistrar(rg);
        var autoStart = new AutoStartRegistrar(rg);
        var ops = new ShellOperations(shortcuts, protocol, uninstall, ms,
            fileAssoc: null, autoStart: autoStart);

        var r = ops.Install(installRoot, AppVersion, createDesktopShortcut: false);

        Assert.True(r.AutoStartRegistered);
        var cmd = rg.GetString(AutoStartRegistrar.RootSubKey, AutoStartRegistrar.ValueName);
        Assert.NotNull(cmd);
        Assert.Contains("--headless", cmd);

        // And Inspect reflects it.
        var status = ops.Inspect(installRoot);
        Assert.True(status.AutoStartRegistered);
    }

    // ---- 7. ShellOperations without a registrar never writes the value. ----
    [Fact]
    public void Install_WithoutAutoStartRegistrar_DoesNotWriteRunValue()
    {
        var installRoot = FreshInstallRoot();
        var sc = new InMemoryShortcutWriter();
        var rg = new InMemoryRegistryWriter();
        var ms = new ShortcutManifestStore();
        var startFolder = Path.Combine(installRoot, "StartMenu");
        var desktopFolder = Path.Combine(installRoot, "Desktop");
        var shortcuts = new ShellRegistrar(sc, ms,
            startMenuFolderProvider: () => startFolder,
            desktopFolderProvider: () => desktopFolder);
        var protocol = new ProtocolRegistrar(rg);
        var uninstall = new UninstallRegistrar(rg);
        var ops = new ShellOperations(shortcuts, protocol, uninstall, ms);

        var r = ops.Install(installRoot, AppVersion, createDesktopShortcut: false);

        Assert.False(r.AutoStartRegistered);
        Assert.Null(rg.GetString(AutoStartRegistrar.RootSubKey, AutoStartRegistrar.ValueName));
    }
}
