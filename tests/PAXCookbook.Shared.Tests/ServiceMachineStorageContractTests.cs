using System;
using System.IO;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

// ===========================================================================
// CYCLE 51 - FIXED SERVICE MACHINE-STORAGE NAME AUTHORITY
// (cycle 58 added the fourth name; cycle 63 added the fifth)
// ===========================================================================
//
// SCOPE, stated plainly. ServiceMachineStorageContract is a PURE, PORTABLE set
// of five fixed names (the fourth, InstallationAnchorFileName, added in cycle
// 58; the fifth, RuntimeDataFolderName, added in cycle 63): no path
// composition, no environment-variable read, no filesystem access, no
// configurable or settable value. It does not resolve %ProgramData%, does not
// open a file, and does not touch the Service directory, the Runtime
// directory, the ownership ledger or the installation anchor.
//
// CYCLE 63 - WHAT THE FIFTH NAME IS FOR. RuntimeDataFolderName names the
// service's separate WRITABLE runtime subdirectory, so the service account can
// be granted write access to its own runtime documents while holding no write
// authority over the metadata directory that carries the ownership records.
// This class asserts the NAME only; it grants and proves no capability.
public class ServiceMachineStorageContractTests
{
    [Fact]
    public void The_six_fixed_names_are_unchanged()
    {
        Assert.Equal("PAXCookbook", ServiceMachineStorageContract.MachineRootFolderName);
        Assert.Equal("Service", ServiceMachineStorageContract.ServiceDataFolderName);
        Assert.Equal("ownership-ledger.json", ServiceMachineStorageContract.OwnershipLedgerFileName);
        Assert.Equal("installation-anchor.json", ServiceMachineStorageContract.InstallationAnchorFileName);
        Assert.Equal("Runtime", ServiceMachineStorageContract.RuntimeDataFolderName);
        Assert.Equal("Recipes", ServiceMachineStorageContract.MachineRecipeStoreFolderName);
    }

    [Fact]
    public void The_machine_recipe_store_name_is_a_plain_leaf_and_is_distinct_from_every_other_name()
    {
        string recipes = ServiceMachineStorageContract.MachineRecipeStoreFolderName;

        // A leaf, never a path fragment: nothing here may traverse or re-root.
        Assert.False(string.IsNullOrWhiteSpace(recipes));
        Assert.DoesNotContain("..", recipes, StringComparison.Ordinal);
        Assert.DoesNotContain("/", recipes, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", recipes, StringComparison.Ordinal);
        Assert.DoesNotContain(":", recipes, StringComparison.Ordinal);

        // The promoted-Recipe store is a SEPARATE namespace from the metadata
        // directory, the service-writable runtime directory, and both ownership
        // record leaves.
        Assert.NotEqual(ServiceMachineStorageContract.MachineRootFolderName, recipes);
        Assert.NotEqual(ServiceMachineStorageContract.ServiceDataFolderName, recipes);
        Assert.NotEqual(ServiceMachineStorageContract.RuntimeDataFolderName, recipes);
        Assert.NotEqual(ServiceMachineStorageContract.OwnershipLedgerFileName, recipes);
        Assert.NotEqual(ServiceMachineStorageContract.InstallationAnchorFileName, recipes);
    }

    [Fact]
    public void The_runtime_folder_name_is_a_plain_leaf_and_is_not_any_other_fixed_name()
    {
        string runtime = ServiceMachineStorageContract.RuntimeDataFolderName;

        // A leaf, never a path fragment: nothing here may traverse or re-root.
        Assert.False(string.IsNullOrWhiteSpace(runtime));
        Assert.DoesNotContain("..", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("/", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", runtime, StringComparison.Ordinal);
        Assert.DoesNotContain(":", runtime, StringComparison.Ordinal);

        // The runtime directory must remain DISTINCT from the metadata directory
        // and from both ownership-record leaf names, because the whole point of
        // the cycle-63 separation is that they are different namespaces.
        Assert.NotEqual(ServiceMachineStorageContract.ServiceDataFolderName, runtime);
        Assert.NotEqual(ServiceMachineStorageContract.OwnershipLedgerFileName, runtime);
        Assert.NotEqual(ServiceMachineStorageContract.InstallationAnchorFileName, runtime);
        Assert.NotEqual(ServiceMachineStorageContract.MachineRootFolderName, runtime);
    }

    [Fact]
    public void The_ownership_ledger_name_is_derived_from_the_ledger_contract()
    {
        Assert.Equal(
            ServiceOwnershipLedgerContract.LedgerFileName,
            ServiceMachineStorageContract.OwnershipLedgerFileName);
    }

    [Fact]
    public void The_type_is_static_and_exposes_only_the_six_fixed_constants()
    {
        Type type = typeof(ServiceMachineStorageContract);
        Assert.True(type.IsAbstract && type.IsSealed, "the contract must be a static class");

        var fields = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.Equal(6, fields.Length);
        foreach (var field in fields)
        {
            Assert.True(field.IsLiteral, field.Name + " must be a compile-time constant");
            Assert.Equal(typeof(string), field.FieldType);
        }

        // No instance surface, no method, no property: six constants only.
        Assert.Empty(type.GetProperties());
        Assert.Empty(type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly));
    }

    [Fact]
    public void The_contract_file_never_composes_a_path_reads_the_environment_or_touches_the_filesystem()
    {
        string path = Path.Combine(
            RepoRoot(), "src", "PAXCookbook.Shared", "Contracts", "ServiceMachineStorageContract.cs");
        Assert.True(File.Exists(path), "the contract file was expected to exist at its authored path");

        string code = StripCommentLines(File.ReadAllText(path));
        foreach (string token in new[]
                 {
                     "Path.Combine", "Path.GetFullPath", "Environment.GetFolderPath", "Environment.GetEnvironmentVariable",
                     "SpecialFolder", "File.", "Directory.", "Registry",
                 })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_contract_is_portable_and_never_references_a_windows_only_type()
    {
        // Service (net8.0, not net8.0-windows) compile-links this file, so it must
        // never reference a Windows-only type.
        string path = Path.Combine(
            RepoRoot(), "src", "PAXCookbook.Shared", "Contracts", "ServiceMachineStorageContract.cs");
        string code = StripCommentLines(File.ReadAllText(path));

        foreach (string token in new[]
                 {
                     "System.Security", "Microsoft.Win32", "System.Runtime.InteropServices", "DllImport",
                 })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }
    }

    // The contract file's own header comment documents, in prose, exactly the
    // tokens this class must prove are ABSENT FROM THE CODE (e.g. "never
    // references System.Environment.SpecialFolder"). A naive substring scan of
    // the raw file would therefore false-positive on its own documentation, so
    // every line beginning with "//" (after leading whitespace) is removed
    // before scanning.
    private static string StripCommentLines(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        foreach (string line in source.Split('\n'))
        {
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    internal static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PAXCookbook.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
