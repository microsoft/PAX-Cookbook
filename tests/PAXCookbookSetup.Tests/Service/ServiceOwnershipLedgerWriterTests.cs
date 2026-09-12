using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 88 - THE FIXED DURABLE LEDGER WRITE SURFACE
// ===========================================================================
//
// SCOPE. Every disk operation below happens inside a FRESH OS-TEMP CONTAINMENT
// ROOT created and deleted by the test. Nothing here touches %ProgramData%, a
// certificate store, a private key, an ACL, the registry, a service, a socket or
// a process. Every SID, thumbprint, key identity and descriptor is SYNTHETIC.
public sealed class ServiceOwnershipLedgerWriterTests : IDisposable
{
    private readonly string containmentRoot;

    public ServiceOwnershipLedgerWriterTests()
    {
        containmentRoot = Path.Combine(
            Path.GetTempPath(), "pax88-writer-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(containmentRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(containmentRoot))
            {
                Directory.Delete(containmentRoot, recursive: true);
            }
        }
        catch (Exception)
        {
            // A stranded temp directory is harmless; failing Dispose is not.
        }
    }

    private string ServiceDirectory => Path.Combine(
        containmentRoot,
        ServiceMachineStorageContract.MachineRootFolderName,
        ServiceMachineStorageContract.ServiceDataFolderName);

    private string LedgerPath => Path.Combine(
        ServiceDirectory, ServiceMachineStorageContract.OwnershipLedgerFileName);

    private void CreateServiceDirectory() => Directory.CreateDirectory(ServiceDirectory);

    // ---- the payload comes from the pure transition authority ----------------

    private static ServiceOwnershipTransitionResult Intent() =>
        ServiceOwnershipPromotionFixtures.BeginIntent();

    private static ServiceOwnershipTransitionResult Mutated() =>
        ServiceOwnershipPromotionFixtures.RecordMutated();

    // =======================================================================
    // THE HAPPY PATH
    // =======================================================================

    [Fact]
    public void An_accepted_document_is_written_reread_and_verified()
    {
        CreateServiceDirectory();
        ServiceOwnershipTransitionResult intent = Intent();

        ServiceOwnershipLedgerWriteResult result = ServiceOwnershipLedgerWriter.WriteWithinContainmentRoot(
            containmentRoot, intent.Serialized, intent.LedgerOutcome, intent.Generation);

        Assert.Equal(ServiceOwnershipLedgerWriteOutcome.Written, result.Outcome);
        Assert.True(result.IsWritten);
        Assert.False(result.PriorStateRestored);

        Assert.True(File.Exists(LedgerPath));
        Assert.Equal(intent.Serialized!.Utf8Bytes, File.ReadAllBytes(LedgerPath));
    }

    [Fact]
    public void The_written_bytes_carry_no_byte_order_mark_and_revalidate_from_disk()
    {
        CreateServiceDirectory();
        ServiceOwnershipTransitionResult intent = Intent();

        Assert.True(ServiceOwnershipLedgerWriter.WriteWithinContainmentRoot(
            containmentRoot, intent.Serialized, intent.LedgerOutcome, intent.Generation).IsWritten);

        byte[] onDisk = File.ReadAllBytes(LedgerPath);
        Assert.False(onDisk.Length >= 3 && onDisk[0] == 0xEF && onDisk[1] == 0xBB && onDisk[2] == 0xBF);

        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        ServiceOwnershipLedgerValidationResult reread =
            ServiceOwnershipLedgerValidator.Validate(strict.GetString(onDisk));

        Assert.True(reread.IsAccepted);
        Assert.Equal(intent.LedgerOutcome, reread.Outcome);
        Assert.Equal(intent.Generation, reread.Document!.Generation);
    }

    [Fact]
    public void A_second_write_replaces_the_first_and_leaves_no_staging_file_behind()
    {
        CreateServiceDirectory();

        ServiceOwnershipTransitionResult intent = Intent();
        Assert.True(ServiceOwnershipLedgerWriter.WriteWithinContainmentRoot(
            containmentRoot, intent.Serialized, intent.LedgerOutcome, intent.Generation).IsWritten);

        ServiceOwnershipTransitionResult mutated = Mutated();
        Assert.True(ServiceOwnershipLedgerWriter.WriteWithinContainmentRoot(
            containmentRoot, mutated.Serialized, mutated.LedgerOutcome, mutated.Generation).IsWritten);

        Assert.Equal(mutated.Serialized!.Utf8Bytes, File.ReadAllBytes(LedgerPath));

        // EXACTLY ONE file in the directory: no staging file and no backup survive.
        string[] leaves = Directory.GetFileSystemEntries(ServiceDirectory);
        string only = Assert.Single(leaves);
        Assert.Equal(
            ServiceMachineStorageContract.OwnershipLedgerFileName,
            Path.GetFileName(only),
            StringComparer.Ordinal);
    }

    // =======================================================================
    // THE DETERMINISTIC FAILPOINT MATRIX
    // =======================================================================

    [Fact]
    public void The_parent_directory_is_never_created()
    {
        // Deliberately NOT calling CreateServiceDirectory.
        ServiceOwnershipTransitionResult intent = Intent();

        ServiceOwnershipLedgerWriteResult result = ServiceOwnershipLedgerWriter.WriteWithinContainmentRoot(
            containmentRoot, intent.Serialized, intent.LedgerOutcome, intent.Generation);

        Assert.Equal(ServiceOwnershipLedgerWriteOutcome.ParentDirectoryUnavailable, result.Outcome);
        Assert.False(Directory.Exists(ServiceDirectory));
        Assert.False(File.Exists(LedgerPath));
    }

    [Fact]
    public void A_null_or_unserialized_source_never_reaches_the_filesystem()
    {
        CreateServiceDirectory();

        Assert.Equal(
            ServiceOwnershipLedgerWriteOutcome.SourceNotSerialized,
            ServiceOwnershipLedgerWriter.WriteWithinContainmentRoot(
                containmentRoot, null, ServiceOwnershipLedgerOutcome.InProgress, 1).Outcome);

        Assert.Equal(
            ServiceOwnershipLedgerWriteOutcome.SourceNotSerialized,
            ServiceOwnershipLedgerWriter.WriteWithinContainmentRoot(
                containmentRoot,
                ServiceOwnershipLedgerSerializer.Serialize(null),
                ServiceOwnershipLedgerOutcome.InProgress,
                1).Outcome);

        Assert.Empty(Directory.GetFileSystemEntries(ServiceDirectory));
    }

    [Fact]
    public void A_wrong_expected_outcome_is_refused_before_any_disk_contact()
    {
        CreateServiceDirectory();
        ServiceOwnershipTransitionResult intent = Intent();

        ServiceOwnershipLedgerWriteResult result = ServiceOwnershipLedgerWriter.WriteWithinContainmentRoot(
            containmentRoot, intent.Serialized, ServiceOwnershipLedgerOutcome.Active, intent.Generation);

        Assert.Equal(ServiceOwnershipLedgerWriteOutcome.ExpectedOutcomeMismatch, result.Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(ServiceDirectory));
    }

    [Fact]
    public void A_wrong_expected_generation_is_refused_before_any_disk_contact()
    {
        CreateServiceDirectory();
        ServiceOwnershipTransitionResult intent = Intent();

        ServiceOwnershipLedgerWriteResult result = ServiceOwnershipLedgerWriter.WriteWithinContainmentRoot(
            containmentRoot, intent.Serialized, intent.LedgerOutcome, intent.Generation + 1);

        Assert.Equal(ServiceOwnershipLedgerWriteOutcome.ExpectedGenerationMismatch, result.Outcome);
        Assert.Empty(Directory.GetFileSystemEntries(ServiceDirectory));
    }

    [Fact]
    public void A_directory_standing_where_the_ledger_belongs_fails_without_destroying_it()
    {
        CreateServiceDirectory();
        Directory.CreateDirectory(LedgerPath);

        ServiceOwnershipTransitionResult intent = Intent();
        ServiceOwnershipLedgerWriteResult result = ServiceOwnershipLedgerWriter.WriteWithinContainmentRoot(
            containmentRoot, intent.Serialized, intent.LedgerOutcome, intent.Generation);

        Assert.NotEqual(ServiceOwnershipLedgerWriteOutcome.Written, result.Outcome);

        // The obstruction is still there, untouched: nothing was deleted to make
        // room, and no staging file survived the failure.
        Assert.True(Directory.Exists(LedgerPath));
        string only = Assert.Single(Directory.GetFileSystemEntries(ServiceDirectory));
        Assert.Equal(LedgerPath, only, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_prior_document_survives_a_refused_write_byte_for_byte()
    {
        CreateServiceDirectory();
        ServiceOwnershipTransitionResult intent = Intent();
        Assert.True(ServiceOwnershipLedgerWriter.WriteWithinContainmentRoot(
            containmentRoot, intent.Serialized, intent.LedgerOutcome, intent.Generation).IsWritten);

        byte[] before = File.ReadAllBytes(LedgerPath);

        ServiceOwnershipTransitionResult mutated = Mutated();
        ServiceOwnershipLedgerWriteResult refused = ServiceOwnershipLedgerWriter.WriteWithinContainmentRoot(
            containmentRoot, mutated.Serialized, mutated.LedgerOutcome, mutated.Generation + 99);

        Assert.Equal(ServiceOwnershipLedgerWriteOutcome.ExpectedGenerationMismatch, refused.Outcome);
        Assert.Equal(before, File.ReadAllBytes(LedgerPath));
    }

    /// <summary>
    /// A reparse point anywhere on the derived chain must refuse. Creating a
    /// directory symbolic link needs Developer Mode or elevation, so this test
    /// asserts the REAL refusal when the link can be created and falls back to
    /// asserting the check is structurally present and ordered before staging when
    /// it cannot. Which branch ran is stated in the assertion messages rather than
    /// silently skipped.
    /// </summary>
    [Fact]
    public void A_reparse_point_on_the_derived_chain_refuses_the_write()
    {
        string realService = Path.Combine(
            containmentRoot, "real", ServiceMachineStorageContract.ServiceDataFolderName);
        Directory.CreateDirectory(realService);

        string machineRoot = Path.Combine(
            containmentRoot, ServiceMachineStorageContract.MachineRootFolderName);
        Directory.CreateDirectory(machineRoot);

        bool linked;
        try
        {
            Directory.CreateSymbolicLink(
                Path.Combine(machineRoot, ServiceMachineStorageContract.ServiceDataFolderName),
                realService);
            linked = Directory.Exists(ServiceDirectory);
        }
        catch (Exception)
        {
            linked = false;
        }

        ServiceOwnershipTransitionResult intent = Intent();

        if (linked)
        {
            ServiceOwnershipLedgerWriteResult result = ServiceOwnershipLedgerWriter.WriteWithinContainmentRoot(
                containmentRoot, intent.Serialized, intent.LedgerOutcome, intent.Generation);

            Assert.Equal(ServiceOwnershipLedgerWriteOutcome.ReparsePointRefused, result.Outcome);
            Assert.False(File.Exists(Path.Combine(realService, ServiceMachineStorageContract.OwnershipLedgerFileName)));
            return;
        }

        // FALLBACK BRANCH, stated honestly: this host would not create a directory
        // symbolic link, so the behaviour is proven structurally instead.
        string source = WriterSource();
        int reparseCheck = source.IndexOf("ChainHasReparsePoint(", StringComparison.Ordinal);
        int staging = source.IndexOf("FileMode.CreateNew", StringComparison.Ordinal);

        Assert.True(reparseCheck > 0, "the reparse check is missing from the writer");
        Assert.True(staging > 0, "the staging write is missing from the writer");
        Assert.True(reparseCheck < staging, "the reparse check must run BEFORE any staging write");
    }

    // =======================================================================
    // STRUCTURAL CONTAINMENT
    // =======================================================================

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PAXCookbook.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string WriterPath() => Path.Combine(
        RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipLedgerWriter.cs");

    private static string WriterSource() => File.ReadAllText(WriterPath());

    [Fact]
    public void The_production_verb_carries_no_path_bearing_parameter()
    {
        MethodInfo write = typeof(ServiceOwnershipLedgerWriter).GetMethod(
            "Write", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;

        Assert.NotNull(write);
        Assert.Equal(3, write.GetParameters().Length);
        Assert.DoesNotContain(write.GetParameters(), p => p.ParameterType == typeof(string));

        // ...and there is no overload that accepts one.
        MethodInfo[] writes = typeof(ServiceOwnershipLedgerWriter)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "Write")
            .ToArray();
        Assert.Single(writes);
    }

    [Fact]
    public void Exactly_one_named_containment_root_seam_exists()
    {
        MethodInfo[] declared = typeof(ServiceOwnershipLedgerWriter)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .ToArray();

        MethodInfo[] withContainmentRoot = declared
            .Where(m => m.GetParameters().Any(p =>
                p.ParameterType == typeof(string)
                && string.Equals(p.Name, "containmentRoot", StringComparison.Ordinal)))
            .ToArray();

        MethodInfo seam = Assert.Single(withContainmentRoot);
        Assert.Equal("WriteWithinContainmentRoot", seam.Name);
        Assert.True(seam.IsAssembly, "the seam must be internal, not public");

        // No OTHER method on the type takes a string at all, so there is no second,
        // unnamed way to hand this surface a location.
        MethodInfo[] otherStringTakers = declared
            .Where(m => !string.Equals(m.Name, "WriteWithinContainmentRoot", StringComparison.Ordinal))
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(string)))
            .Where(m => !m.IsPrivate)
            .ToArray();
        Assert.Empty(otherStringTakers);
    }

    [Fact]
    public void The_writer_names_no_reader_type_anywhere_including_comments()
    {
        string raw = WriterSource();

        foreach (string readerType in new[]
                 {
                     "ServiceOwnershipLedgerReader",
                     "ServiceOwnershipLedgerReaderInterpreter",
                     "ServiceOwnershipLedgerReadResult",
                     "ServiceOwnershipLedgerReadState",
                     "ServiceOwnershipLedgerReadFacts",
                 })
        {
            Assert.DoesNotContain(readerType, raw, StringComparison.Ordinal);
        }

        // POSITIVE CONTROL: the raw scan really did read this file.
        Assert.Contains("class ServiceOwnershipLedgerWriter", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void The_writer_derives_every_name_from_the_one_storage_name_authority()
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(WriterSource());

        Assert.Contains("ServiceMachineStorageContract.MachineRootFolderName", code, StringComparison.Ordinal);
        Assert.Contains("ServiceMachineStorageContract.ServiceDataFolderName", code, StringComparison.Ordinal);
        Assert.Contains("ServiceMachineStorageContract.OwnershipLedgerFileName", code, StringComparison.Ordinal);

        // NO RETYPED SPELLING. This check runs over comment-stripped source that
        // STILL CARRIES STRING LITERALS, because ExtractCode removes literals and
        // would make the negative below vacuous.
        string withLiterals = StripCommentsKeepingLiterals(WriterSource());

        Assert.DoesNotContain("\"PAXCookbook\"", withLiterals, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Service\"", withLiterals, StringComparison.Ordinal);
        Assert.DoesNotContain("ownership-ledger.json", withLiterals, StringComparison.Ordinal);

        // POSITIVE CONTROL: the literal-preserving stripper really does keep
        // literals, so the three negatives above are falsifiable.
        Assert.Contains(
            "\"PAXCookbook\"",
            StripCommentsKeepingLiterals("// PAXCookbook\nclass X { string s = \"PAXCookbook\"; }"),
            StringComparison.Ordinal);
    }

    private static string StripCommentsKeepingLiterals(string source)
    {
        var sb = new StringBuilder(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }
                sb.Append('\n');
                continue;
            }

            if (i + 1 < source.Length && source[i] == '/' && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }
                i++;
                continue;
            }

            sb.Append(source[i]);
        }
        return sb.ToString();
    }

    [Theory]
    [InlineData("X509")]
    [InlineData("CngKey")]
    [InlineData("AccessControl")]
    [InlineData("FileSystemAccessRule")]
    [InlineData("SetAccessControl")]
    [InlineData("RegistryKey")]
    [InlineData("Process.Start")]
    [InlineData("ProcessStartInfo")]
    [InlineData("HttpClient")]
    [InlineData("Socket")]
    [InlineData("OpenSCManager")]
    [InlineData("CreateService")]
    [InlineData("WindowsIdentity")]
    [InlineData("PAX_Purview")]
    [InlineData("StartBake")]
    public void The_writer_contains_no_forbidden_capability(string token)
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(WriterSource());

        Assert.False(string.IsNullOrWhiteSpace(code));
        Assert.DoesNotContain(token, code, StringComparison.Ordinal);

        // POSITIVE CONTROL: the same scan DOES find the token when it is present.
        Assert.Contains(
            token,
            SetupCSharpLexicalScanner.ExtractCode("class X { void M() { var y = " + token + " ; } }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void No_production_source_outside_the_writer_calls_the_writer()
    {
        string[] sources = Directory
            .GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.NotEmpty(sources);

        var offenders = new List<string>();
        int calibration = 0;

        foreach (string file in sources)
        {
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(WriterPath()), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(file));
            if (code.Contains("ServiceOwnershipLedgerContract", StringComparison.Ordinal))
            {
                calibration++;
            }
            if (code.Contains("ServiceOwnershipLedgerWriter", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.True(calibration > 0, "the positive control failed, so the zero below is not calibrated");
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_result_carries_only_a_bounded_outcome_and_a_boolean()
    {
        PropertyInfo[] properties = typeof(ServiceOwnershipLedgerWriteResult)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .ToArray();

        Assert.Equal(3, properties.Length);

        foreach (PropertyInfo property in properties)
        {
            Assert.True(
                property.PropertyType == typeof(ServiceOwnershipLedgerWriteOutcome)
                || property.PropertyType == typeof(bool),
                property.Name + " is not a bounded value");
        }

        // ToString carries only the bounded token: no path, byte, SID or message.
        Assert.Equal(
            nameof(ServiceOwnershipLedgerWriteOutcome.ParentDirectoryUnavailable),
            ServiceOwnershipLedgerWriteResult.Refused(
                ServiceOwnershipLedgerWriteOutcome.ParentDirectoryUnavailable).ToString());
    }

    [Fact]
    public void Zero_is_never_a_written_outcome()
    {
        Assert.Equal(0, (int)ServiceOwnershipLedgerWriteOutcome.Unspecified);
        Assert.NotEqual(0, (int)ServiceOwnershipLedgerWriteOutcome.Written);
        Assert.False(ServiceOwnershipLedgerWriteResult.Refused(default).IsWritten);
    }

    /// <summary>
    /// RecoveryRequired is reachable ONLY when a rollback cannot be proven, and the
    /// writer never reports it as a restored state. Asserted on the result type,
    /// because the disk path that produces it is not reachable from a sandbox
    /// without an injection seam this cycle is not authorized to add.
    /// </summary>
    [Fact]
    public void Recovery_required_is_never_reported_as_a_restored_prior_state()
    {
        ServiceOwnershipLedgerWriteResult recovery = ServiceOwnershipLedgerWriteResult.Refused(
            ServiceOwnershipLedgerWriteOutcome.RecoveryRequired);

        Assert.False(recovery.IsWritten);
        Assert.False(recovery.PriorStateRestored);

        ServiceOwnershipLedgerWriteResult rolledBack = ServiceOwnershipLedgerWriteResult.RolledBack(
            ServiceOwnershipLedgerWriteOutcome.RereadBytesMismatch);

        Assert.False(rolledBack.IsWritten);
        Assert.True(rolledBack.PriorStateRestored);

        // The source really does route an unprovable restore to RecoveryRequired
        // rather than to a rolled-back result.
        string code = SetupCSharpLexicalScanner.ExtractCode(WriterSource());
        Assert.Contains("RecoveryRequired", code, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.FixedTimeEquals(restored, priorBytes)", code, StringComparison.Ordinal);
    }
}
