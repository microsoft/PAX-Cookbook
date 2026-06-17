using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Shell;

namespace PAXCookbookSetup;

// Builds the production ShellOperations bundle (Win32 shortcut + HKCU
// registry writers, or no-op writers when the shell test gate is active).
// Extracted so BOTH the CLI verb path (Program.cs) and the GUI wizard
// (PAXCookbookSetup.Gui.WizardLauncher) construct shell side-effects
// identically. Returns a fresh instance each call so per-verb state is
// isolated.
public static class ShellOperationsFactory
{
    public static IShellOperations Build()
    {
        IRegistryWriter registry = TestShellGate.IsActive()
            ? new NoOpRegistryWriter() : new HkcuRegistryWriter();
        IShortcutWriter writer = TestShellGate.IsActive()
            ? new NoOpShortcutWriter() : new Win32ShortcutWriter();
        var manifestStore = new ShortcutManifestStore();
        var shellRegistrar = new ShellRegistrar(writer, manifestStore);
        var protocolRegistrar = new ProtocolRegistrar(registry);
        var fileAssociationRegistrar = new FileAssociationRegistrar(registry);
        var uninstallRegistrar = new UninstallRegistrar(registry);
        var autoStartRegistrar = new AutoStartRegistrar(registry);
        return new ShellOperations(shellRegistrar, protocolRegistrar, uninstallRegistrar,
            manifestStore, fileAssociationRegistrar, autoStartRegistrar);
    }
}
