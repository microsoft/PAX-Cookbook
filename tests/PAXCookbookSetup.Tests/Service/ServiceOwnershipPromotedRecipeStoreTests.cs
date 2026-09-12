using System;
using System.Collections.Generic;
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
// CYCLE 82 - FIXED PROMOTED-RECIPE PERSISTENCE (D3)
// ===========================================================================
//
// SCOPE, stated plainly. Every test in this file operates inside a FRESH OS-TEMP
// SANDBOX ROOT created and deleted by the test itself. Nothing here touches
// %ProgramData%, a certificate store, an ACL, the registry, the SCM, a real
// machine Recipe store or any per-user Recipe. No production code calls the
// surface under test, and one test below proves that with a calibrated scan.
//
// THE STRUCTURAL ASSERTIONS ARE REFLECTION-BASED ON PURPOSE. They were authored
// RED, before the production type existed: a compile-time reference to a type
// that does not exist yet does not fail, it stops the whole test assembly
// compiling, which would have destroyed the RED evidence for every other test in
// this project.
public sealed class ServiceOwnershipPromotedRecipeStoreRedTests
{
    private const string StoreTypeName = "PAXCookbookSetup.Service.ServiceOwnershipPromotedRecipeStore";
    private const string ContentTypeName = "PAXCookbookSetup.Service.ServiceOwnershipPromotedRecipeContent";

    [Fact]
    public void A_fixed_promoted_recipe_persistence_surface_exists()
    {
        Assembly setup = typeof(ServiceOwnershipLedgerReaderInterpreter).Assembly;

        Type? store = setup.GetType(StoreTypeName, throwOnError: false);
        Type? content = setup.GetType(ContentTypeName, throwOnError: false);

        Assert.NotNull(store);
        Assert.NotNull(content);
    }

    [Fact]
    public void The_persistence_surface_never_accepts_a_caller_supplied_destination_path()
    {
        Assembly setup = typeof(ServiceOwnershipLedgerReaderInterpreter).Assembly;
        Type? store = setup.GetType(StoreTypeName, throwOnError: false);
        Assert.NotNull(store);

        MethodInfo? persist = store!.GetMethod(
            "Persist", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(persist);

        // Exactly two inputs: a bounded promoted-job id and validated Recipe content.
        // No path, no directory, no root, no file name, no descriptor.
        ParameterInfo[] parameters = persist!.GetParameters();
        Assert.Equal(2, parameters.Length);
        foreach (ParameterInfo parameter in parameters)
        {
            Assert.DoesNotContain("path", parameter.Name!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("directory", parameter.Name!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("root", parameter.Name!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("file", parameter.Name!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Recipe_content_can_only_be_produced_by_a_validating_factory()
    {
        Assembly setup = typeof(ServiceOwnershipLedgerReaderInterpreter).Assembly;
        Type? content = setup.GetType(ContentTypeName, throwOnError: false);
        Assert.NotNull(content);

        MethodInfo? tryCreate = content!.GetMethod(
            "TryCreate", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(tryCreate);

        // No public or internal constructor may hand out unvalidated content.
        ConstructorInfo[] constructors = content.GetConstructors(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (ConstructorInfo constructor in constructors)
        {
            Assert.True(
                constructor.IsPrivate,
                "every promoted-Recipe content constructor must be private");
        }
    }

    // =======================================================================
    // CONTENT VALIDATION
    // =======================================================================

    [Fact]
    public void A_well_formed_recipe_object_is_accepted_and_digested()
    {
        byte[] bytes = Utf8("{\"name\":\"synthetic\",\"steps\":[]}");

        Assert.True(ServiceOwnershipPromotedRecipeContent.TryCreate(
            bytes, out ServiceOwnershipPromotedRecipeContent content));
        Assert.True(content.IsValidated);
        Assert.Equal(UpperHex(SHA256.HashData(bytes)), content.Sha256);

        // The caller's array is never retained: mutating it must not change content.
        bytes[2] = (byte)'X';
        Assert.NotEqual(UpperHex(SHA256.HashData(bytes)), content.Sha256);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    [InlineData("{\"unterminated\":")]
    public void A_malformed_recipe_is_refused(string text)
    {
        Assert.False(ServiceOwnershipPromotedRecipeContent.TryCreate(
            Utf8(text), out ServiceOwnershipPromotedRecipeContent _));
    }

    [Fact]
    public void A_recipe_with_a_byte_order_mark_or_invalid_utf8_is_refused()
    {
        byte[] body = Utf8("{\"a\":1}");
        var withBom = new byte[3 + body.Length];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        Buffer.BlockCopy(body, 0, withBom, 3, body.Length);

        Assert.False(ServiceOwnershipPromotedRecipeContent.TryCreate(
            withBom, out ServiceOwnershipPromotedRecipeContent _));

        byte[] invalidUtf8 = { (byte)'{', 0xC3, 0x28, (byte)'}' };
        Assert.False(ServiceOwnershipPromotedRecipeContent.TryCreate(
            invalidUtf8, out ServiceOwnershipPromotedRecipeContent _));
    }

    [Fact]
    public void A_null_or_oversized_recipe_is_refused()
    {
        Assert.False(ServiceOwnershipPromotedRecipeContent.TryCreate(
            null, out ServiceOwnershipPromotedRecipeContent _));

        var oversized = new byte[ServiceOwnershipPromotedRecipeContent.MaxRecipeBytes + 1];
        Assert.False(ServiceOwnershipPromotedRecipeContent.TryCreate(
            oversized, out ServiceOwnershipPromotedRecipeContent _));
    }

    // =======================================================================
    // PERSISTENCE - THE DERIVED DESTINATION
    // =======================================================================

    [Fact]
    public void A_persisted_recipe_lands_at_the_internally_derived_fixed_destination()
    {
        using var sandbox = new Sandbox();

        ServiceOwnershipPromotedRecipePersistResult result =
            sandbox.Store.Persist("job-0001", Content("{\"name\":\"synthetic\"}"));

        Assert.Equal(ServiceOwnershipPromotedRecipePersistOutcome.Persisted, result.Outcome);
        Assert.True(result.IsPersisted);
        Assert.Equal("job-0001", result.PromotedJobId);

        // The destination is recomputed HERE from the fixed name authority, not
        // taken from the result, so this proves the derivation rather than echoing
        // it. The result deliberately carries no path at all.
        string expected = Path.Combine(
            sandbox.Root,
            ServiceMachineStorageContract.MachineRootFolderName,
            ServiceMachineStorageContract.ServiceDataFolderName,
            ServiceMachineStorageContract.MachineRecipeStoreFolderName,
            "job-0001.json");

        Assert.True(File.Exists(expected), "the promoted Recipe was not at the derived destination");
        Assert.Equal(
            UpperHex(SHA256.HashData(File.ReadAllBytes(expected))),
            result.ContentSha256);
    }

    [Fact]
    public void The_persist_result_never_carries_a_path()
    {
        foreach (PropertyInfo property in typeof(ServiceOwnershipPromotedRecipePersistResult)
                     .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            Assert.DoesNotContain("path", property.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("directory", property.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void No_staging_file_survives_a_successful_persist()
    {
        using var sandbox = new Sandbox();

        Assert.True(sandbox.Store.Persist("job-0001", Content("{\"a\":1}")).IsPersisted);

        Assert.Empty(Directory.GetFiles(sandbox.StoreDirectory, "*.tmp"));
        Assert.Single(Directory.GetFiles(sandbox.StoreDirectory));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape")]
    [InlineData("..\\escape")]
    [InlineData("C:\\Windows\\System32\\job")]
    [InlineData("job/0001")]
    [InlineData("job:0001")]
    [InlineData("job*0001")]
    [InlineData("\\\\server\\share\\job")]
    public void A_job_id_that_could_name_anything_but_a_bounded_leaf_is_refused(string? jobId)
    {
        using var sandbox = new Sandbox();

        ServiceOwnershipPromotedRecipePersistResult result =
            sandbox.Store.Persist(jobId, Content("{\"a\":1}"));

        Assert.Equal(
            ServiceOwnershipPromotedRecipePersistOutcome.InvalidPromotedJobId, result.Outcome);

        // Nothing at all was created: a refused job id must not even establish the store.
        Assert.Empty(Directory.GetFiles(sandbox.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Unvalidated_content_can_never_be_persisted()
    {
        using var sandbox = new Sandbox();

        ServiceOwnershipPromotedRecipePersistResult result =
            sandbox.Store.Persist("job-0001", default);

        Assert.Equal(
            ServiceOwnershipPromotedRecipePersistOutcome.InvalidRecipeContent, result.Outcome);
        Assert.Empty(Directory.GetFiles(sandbox.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_collision_at_the_derived_destination_is_refused_and_never_overwritten()
    {
        using var sandbox = new Sandbox();

        Assert.True(sandbox.Store.Persist("job-0001", Content("{\"first\":true}")).IsPersisted);
        string destination = Path.Combine(sandbox.StoreDirectory, "job-0001.json");
        byte[] before = File.ReadAllBytes(destination);

        ServiceOwnershipPromotedRecipePersistResult second =
            sandbox.Store.Persist("job-0001", Content("{\"second\":true}"));

        Assert.Equal(
            ServiceOwnershipPromotedRecipePersistOutcome.DestinationCollision, second.Outcome);
        Assert.Equal(before, File.ReadAllBytes(destination));
    }

    [Fact]
    public void An_unknown_sibling_in_the_store_refuses_the_whole_persist()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.StoreDirectory);
        File.WriteAllText(Path.Combine(sandbox.StoreDirectory, "not-a-promoted-recipe.txt"), "x");

        ServiceOwnershipPromotedRecipePersistResult result =
            sandbox.Store.Persist("job-0001", Content("{\"a\":1}"));

        Assert.Equal(
            ServiceOwnershipPromotedRecipePersistOutcome.UnknownSiblingPresent, result.Outcome);
        Assert.False(File.Exists(Path.Combine(sandbox.StoreDirectory, "job-0001.json")));
    }

    [Fact]
    public void An_unknown_child_directory_in_the_store_refuses_the_whole_persist()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(Path.Combine(sandbox.StoreDirectory, "unexpected"));

        ServiceOwnershipPromotedRecipePersistResult result =
            sandbox.Store.Persist("job-0001", Content("{\"a\":1}"));

        Assert.Equal(
            ServiceOwnershipPromotedRecipePersistOutcome.UnknownSiblingPresent, result.Outcome);
    }

    // =======================================================================
    // COMPENSATION
    // =======================================================================

    [Fact]
    public void Compensation_removes_only_the_transaction_created_promoted_recipe()
    {
        using var sandbox = new Sandbox();

        ServiceOwnershipPromotedRecipePersistResult receipt =
            sandbox.Store.Persist("job-0001", Content("{\"a\":1}"));
        Assert.True(receipt.IsPersisted);

        string destination = Path.Combine(sandbox.StoreDirectory, "job-0001.json");
        Assert.True(File.Exists(destination));

        Assert.Equal(
            ServiceOwnershipPromotedRecipeCompensationOutcome.Removed,
            sandbox.Store.Compensate(receipt));
        Assert.False(File.Exists(destination));

        // Compensating twice is clean, not an error.
        Assert.Equal(
            ServiceOwnershipPromotedRecipeCompensationOutcome.NothingToRemove,
            sandbox.Store.Compensate(receipt));
    }

    [Fact]
    public void Compensation_leaves_a_file_it_did_not_write_exactly_where_it_is()
    {
        using var sandbox = new Sandbox();

        ServiceOwnershipPromotedRecipePersistResult receipt =
            sandbox.Store.Persist("job-0001", Content("{\"a\":1}"));
        Assert.True(receipt.IsPersisted);

        // Somebody else replaced the contents after the receipt was issued.
        string destination = Path.Combine(sandbox.StoreDirectory, "job-0001.json");
        File.WriteAllBytes(destination, Utf8("{\"someone\":\"else\"}"));

        Assert.Equal(
            ServiceOwnershipPromotedRecipeCompensationOutcome.NotTransactionCreated,
            sandbox.Store.Compensate(receipt));
        Assert.True(File.Exists(destination));
        Assert.Equal("{\"someone\":\"else\"}", File.ReadAllText(destination));
    }

    [Fact]
    public void Compensation_refuses_a_receipt_that_did_not_come_from_a_persist()
    {
        using var sandbox = new Sandbox();

        Assert.Equal(
            ServiceOwnershipPromotedRecipeCompensationOutcome.InvalidReceipt,
            sandbox.Store.Compensate(null));

        ServiceOwnershipPromotedRecipePersistResult refused =
            sandbox.Store.Persist("job/0001", Content("{\"a\":1}"));
        Assert.False(refused.IsPersisted);
        Assert.Equal(
            ServiceOwnershipPromotedRecipeCompensationOutcome.InvalidReceipt,
            sandbox.Store.Compensate(refused));
    }

    [Fact]
    public void Compensation_never_removes_a_sibling_belonging_to_another_job()
    {
        using var sandbox = new Sandbox();

        ServiceOwnershipPromotedRecipePersistResult first =
            sandbox.Store.Persist("job-0001", Content("{\"a\":1}"));
        ServiceOwnershipPromotedRecipePersistResult second =
            sandbox.Store.Persist("job-0002", Content("{\"b\":2}"));
        Assert.True(first.IsPersisted);
        Assert.True(second.IsPersisted);

        Assert.Equal(
            ServiceOwnershipPromotedRecipeCompensationOutcome.Removed,
            sandbox.Store.Compensate(first));

        Assert.False(File.Exists(Path.Combine(sandbox.StoreDirectory, "job-0001.json")));
        Assert.True(File.Exists(Path.Combine(sandbox.StoreDirectory, "job-0002.json")));
    }

    // =======================================================================
    // CONTAINMENT
    // =======================================================================

    [Fact]
    public void The_surface_writes_nothing_outside_the_derived_store()
    {
        using var sandbox = new Sandbox();

        Assert.True(sandbox.Store.Persist("job-0001", Content("{\"a\":1}")).IsPersisted);

        // Only the derived chain exists beneath the sandbox root, and only the one
        // promoted Recipe exists within it.
        string[] all = Directory.GetFiles(sandbox.Root, "*", SearchOption.AllDirectories);
        Assert.Single(all);
        Assert.Equal(Path.Combine(sandbox.StoreDirectory, "job-0001.json"), all[0]);
    }

    [Fact]
    public void The_source_never_mentions_programdata_a_certificate_store_an_acl_or_a_service()
    {
        string path = Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipPromotedRecipeStore.cs");
        Assert.True(File.Exists(path));

        string code = StripCommentLines(File.ReadAllText(path));
        foreach (string token in new[]
                 {
                     "SpecialFolder", "CommonApplicationData", "Registry",
                     "X509", "FileSystemAccessRule", "SetAccessControl",
                     "ServiceController", "Process.Start", "HttpClient", "GetEnvironmentVariable",
                 })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }
    }

    // CYCLE 94 AMENDMENT (G3), UNDER EXPLICIT AUTHORITY.
    //
    // TWO THINGS CHANGED, AND THE SECOND IS A TIGHTENING.
    //   1. The authorized set grew from ONE file to exactly TWO: the store file
    //      and the ONE authorized composition root.
    //   2. The LEAF-NAME exemption is GONE. Cycle 92 compared
    //      Path.GetFileName(file) to a literal, which would have exempted a
    //      relocated or duplicated file with the same leaf anywhere under src/.
    //      It is replaced by WHOLE CANONICAL PATH equality, OrdinalIgnoreCase -
    //      no directory allowance, no pattern, no suffix and no leaf matching.
    //
    // Every other production file must still contain ZERO store references, and
    // both calibration controls are retained.

    private static readonly Func<string, string, bool> ExactFullPathMatch =
        static (actualFullPath, allowedFullPath) =>
            string.Equals(actualFullPath, allowedFullPath, StringComparison.OrdinalIgnoreCase);

    private static string StoreFullPath() => Path.GetFullPath(Path.Combine(
        RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipPromotedRecipeStore.cs"));

    private static string CompositionRootFullPath() => Path.GetFullPath(Path.Combine(
        RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipElevatedTransaction.cs"));

    private static string[] AuthorizedStoreFiles() =>
        new[] { StoreFullPath(), CompositionRootFullPath() };

    /// <summary>
    /// The ONE rule body, shared by the real sweep and by every control, so no
    /// control can pass against a paraphrase of the rule.
    /// </summary>
    private static bool IsStoreOffender(
        string fileFullPath,
        string code,
        IReadOnlyList<string> authorizedFullPaths,
        Func<string, string, bool> fileMatches)
    {
        string canonical = Path.GetFullPath(fileFullPath);
        foreach (string allowed in authorizedFullPaths)
        {
            if (fileMatches(canonical, allowed))
            {
                return false;
            }
        }

        return code.Contains("ServiceOwnershipPromotedRecipeStore", StringComparison.Ordinal);
    }

    private static bool IsStoreOffender(string fileFullPath, string code) =>
        IsStoreOffender(fileFullPath, code, AuthorizedStoreFiles(), ExactFullPathMatch);

    [Fact]
    public void The_store_allowance_is_exactly_two_distinct_canonical_files()
    {
        string[] allowed = AuthorizedStoreFiles();

        Assert.Equal(2, allowed.Length);
        Assert.NotEqual(allowed[0], allowed[1], StringComparer.OrdinalIgnoreCase);
        foreach (string path in allowed)
        {
            Assert.Equal(path, Path.GetFullPath(path), StringComparer.Ordinal);
            Assert.True(File.Exists(path), "an authorized store file is missing: " + path);
        }
    }

    [Fact]
    public void No_production_source_file_calls_the_promoted_recipe_store()
    {
        string src = Path.Combine(RepoRoot(), "src");
        string[] files = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories);

        // POSITIVE CONTROL. The scan must be able to find something, otherwise the
        // zero below would prove nothing at all.
        int controlHits = 0;
        int callSites = 0;
        var referencingFiles = new List<string>();
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            if (text.Contains("namespace ", StringComparison.Ordinal))
            {
                controlHits++;
            }

            string code = StripCommentLines(text);
            if (IsStoreOffender(file, code))
            {
                callSites++;
            }
            if (code.Contains("ServiceOwnershipPromotedRecipeStore", StringComparison.Ordinal))
            {
                referencingFiles.Add(Path.GetFullPath(file));
            }
        }

        Assert.True(files.Length > 100, "the production scan found implausibly few files");
        Assert.True(controlHits > 100, "the positive control failed, so the zero below is not calibrated");
        Assert.Equal(0, callSites);

        // EXACT PRESENCE. Exactly the two authorized files may reference the
        // store, so a third can never be a silent addition.
        Assert.Equal(
            AuthorizedStoreFiles().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            referencingFiles.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_composition_root_constructs_the_store_and_its_fixed_port_exactly_once_each()
    {
        string root = StripCommentLines(File.ReadAllText(CompositionRootFullPath()));

        Assert.Equal(1, CountOccurrences(root, "new ServiceOwnershipPromotedRecipeStore("));
        Assert.Equal(1, CountOccurrences(root, "new ServiceOwnershipFixedPromotedRecipePort("));

        // The store is named ONLY to construct it - no field, no property, no
        // second reference of any kind.
        Assert.Equal(1, CountOccurrences(root, "ServiceOwnershipPromotedRecipeStore"));
    }

    [Fact]
    public void MUTATION_a_leaf_name_exemption_would_let_a_relocated_store_through()
    {
        // THE EXACT DEFECT THIS AMENDMENT REMOVED. A same-leaf copy elsewhere
        // under src/ is an offender under the production rule and is NOT an
        // offender under the old leaf-name comparison.
        string relocated = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "Elsewhere", "ServiceOwnershipPromotedRecipeStore.cs"));
        const string code = "class X { ServiceOwnershipPromotedRecipeStore s; }";

        Assert.True(IsStoreOffender(relocated, code));

        Func<string, string, bool> leafNameMatch = static (actual, allowed) =>
            string.Equals(Path.GetFileName(actual), Path.GetFileName(allowed), StringComparison.OrdinalIgnoreCase);

        Assert.False(IsStoreOffender(relocated, code, AuthorizedStoreFiles(), leafNameMatch));
    }

    [Fact]
    public void MUTATION_widening_the_store_allowance_to_the_whole_service_directory_is_caught()
    {
        string sibling = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "SomeUnrelatedServiceFile.cs"));
        const string code = "class X { ServiceOwnershipPromotedRecipeStore s; }";

        Assert.True(IsStoreOffender(sibling, code));

        Func<string, string, bool> directoryWideMatch = static (actual, allowed) =>
            string.Equals(Path.GetDirectoryName(actual), Path.GetDirectoryName(allowed), StringComparison.OrdinalIgnoreCase);

        Assert.False(IsStoreOffender(sibling, code, AuthorizedStoreFiles(), directoryWideMatch));
    }

    [Fact]
    public void MUTATION_permitting_a_third_store_file_is_caught()
    {
        string probe = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipElevatedTransaction2.cs"));
        const string code = "class X { ServiceOwnershipPromotedRecipeStore s; }";

        Assert.True(IsStoreOffender(probe, code));

        string[] widened = AuthorizedStoreFiles().Append(probe).ToArray();
        Assert.Equal(3, widened.Length);
        Assert.False(IsStoreOffender(probe, code, widened, ExactFullPathMatch));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // =======================================================================
    // CYCLE 86 (F1b) - FILENAME SAFETY AT THE CONSUMER BOUNDARY
    // =======================================================================
    //
    // WHAT THIS PROVES. That every promotedJobId the CYCLE 86 request grammar
    // accepts composes exactly ONE direct child of the derived store, that no
    // component of the composed name is a relative-path component, that the leaf
    // is never hidden, and that the derived PARENT is unchanged by the id. The
    // request parser gains no filesystem capability from any of this; the proof
    // lives here, on the only surface that actually composes a name.

    public static TheoryData<string> RequestAcceptableJobIds() => new()
    {
        "job-0001",
        "job_0001",
        "job.0001",
        "J0b.-_9",
        "a",
        "0",
        "job.0001.v2",
        "job-0001_retry.2",
        // A single TRAILING dot is legal key-identity spelling and is retained
        // deliberately - see the composition proof below.
        "job.",
        // RESERVED DEVICE NAMES. The stricter provider grammar refuses these; the
        // key-identity grammar does not, and this is the EMPIRICAL NEGATIVE RESULT
        // that makes that ruling sound rather than an oversight: the ".json"
        // extension is always appended, so the leaf is never a bare device name.
        "CON",
        "PRN",
        "NUL",
        "COM1",
        "LPT1",
    };

    [Theory]
    [MemberData(nameof(RequestAcceptableJobIds))]
    public void Every_request_acceptable_job_id_composes_exactly_one_direct_child(string jobId)
    {
        // The fixture really is one the request grammar admits.
        Assert.True(
            ServiceOwnershipLedgerContract.IsValidKeyIdentity(jobId),
            "fixture " + jobId + " is not a valid key identity, so it is not a request-acceptable id");

        using var sandbox = new Sandbox();
        string derivedParent = Path.GetFullPath(sandbox.StoreDirectory);

        ServiceOwnershipPromotedRecipePersistResult result =
            sandbox.Store.Persist(jobId, Content("{\"a\":1}"));
        Assert.True(result.IsPersisted, "persist refused with " + result.Outcome);

        // EXACTLY ONE file exists anywhere beneath the sandbox root.
        string only = Assert.Single(
            Directory.GetFiles(sandbox.Root, "*", SearchOption.AllDirectories));

        // The derived PARENT is untouched by the id.
        Assert.Equal(
            derivedParent,
            Path.GetFullPath(Path.GetDirectoryName(only)!),
            StringComparer.OrdinalIgnoreCase);

        // ONE leaf, composed as id + the store's own fixed extension.
        string leaf = Path.GetFileName(only);
        Assert.Equal(jobId + ".json", leaf);

        // NOT hidden: the key-identity grammar refuses a leading dot.
        Assert.NotEqual('.', leaf[0]);

        // NOT a relative-path component, and carries no separator, volume
        // separator or wildcard.
        Assert.NotEqual(".", leaf);
        Assert.NotEqual("..", leaf);
        foreach (char c in new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
        {
            Assert.DoesNotContain(c.ToString(), leaf, StringComparison.Ordinal);
        }

        // The leaf really did survive full path resolution as a direct child.
        Assert.Equal(
            Path.GetFullPath(Path.Combine(derivedParent, leaf)),
            Path.GetFullPath(only),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_trailing_dot_id_yields_a_dot_pair_in_the_leaf_that_is_provably_not_a_traversal()
    {
        // DISCLOSED PRECISELY. "job." is accepted, and "job." + ".json" spells
        // "job..json", which CONTAINS the two-character sequence "..". That
        // sequence is only a traversal when it is a WHOLE path component, and here
        // it never is: the composed leaf resolves to one direct child, and the
        // parent is unchanged. The key-identity grammar already refuses an
        // EMBEDDED "..", so the only way the pair can appear at all is this single
        // trailing dot meeting the extension's leading dot.
        using var sandbox = new Sandbox();
        string derivedParent = Path.GetFullPath(sandbox.StoreDirectory);

        Assert.True(sandbox.Store.Persist("job.", Content("{\"a\":1}")).IsPersisted);

        string only = Assert.Single(
            Directory.GetFiles(sandbox.Root, "*", SearchOption.AllDirectories));
        string leaf = Path.GetFileName(only);

        Assert.Equal("job..json", leaf);
        Assert.Contains("..", leaf, StringComparison.Ordinal);

        // ...and yet no COMPONENT of the resolved path is "." or "..".
        foreach (string component in Path.GetFullPath(only)
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            Assert.NotEqual(".", component);
            Assert.NotEqual("..", component);
        }

        Assert.Equal(
            derivedParent,
            Path.GetFullPath(Path.GetDirectoryName(only)!),
            StringComparer.OrdinalIgnoreCase);

        // An EMBEDDED ".." can never reach here in the first place.
        Assert.False(ServiceOwnershipLedgerContract.IsValidKeyIdentity("job..0001"));
        Assert.False(ServiceOwnershipLedgerContract.IsValidKeyIdentity(".."));
    }

    [Fact]
    public void No_caller_supplies_an_extension_a_leaf_or_a_path_to_the_store()
    {
        MethodInfo persist = typeof(ServiceOwnershipPromotedRecipeStore).GetMethod(
            "Persist", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

        ParameterInfo[] parameters = persist.GetParameters();
        Assert.Equal(2, parameters.Length);
        foreach (ParameterInfo parameter in parameters)
        {
            foreach (string forbidden in new[]
                     {
                         "path", "directory", "root", "file", "name", "extension", "leaf", "store",
                     })
            {
                Assert.DoesNotContain(forbidden, parameter.Name!, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The extension is the STORE'S OWN private constant. It is declared exactly
        // once and is not reachable by any caller.
        string source = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipPromotedRecipeStore.cs"));
        Assert.Contains(
            "private const string PromotedRecipeExtension = \".json\";",
            source,
            StringComparison.Ordinal);

        Assert.Empty(typeof(ServiceOwnershipPromotedRecipeStore)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                        | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.Contains("Extension", StringComparison.OrdinalIgnoreCase)
                        && m is not FieldInfo { IsPrivate: true }));
    }

    [Fact]
    public void The_store_grammar_is_a_SUPERSET_of_the_request_grammar_in_the_direction_that_matters()
    {
        // DISCLOSURE for whoever lands the writer. The store's own job-id rule is
        // the plain bounded token, which is LOOSER than the request grammar: it
        // still admits "." and "..". That is harmless here only because the
        // extension is always appended. What must hold - and is asserted - is the
        // one-way containment: everything the REQUEST accepts, the STORE accepts.
        foreach (string accepted in new[]
                 {
                     "job-0001", "job_0001", "job.0001", "J0b.-_9", "a", "0", "job.", "CON",
                 })
        {
            Assert.True(ServiceOwnershipLedgerContract.IsValidKeyIdentity(accepted));
            Assert.True(ServiceOwnershipLedgerContract.IsValidBoundedToken(
                accepted, ServiceOwnershipLedgerContract.MaxStringLength));
        }

        // The reverse does NOT hold, which is exactly why the narrowing had to
        // happen at the request boundary and not be assumed downstream.
        foreach (string storeOnly in new[] { ".", "..", ".hidden", "..a", "a..b" })
        {
            Assert.True(ServiceOwnershipLedgerContract.IsValidBoundedToken(
                storeOnly, ServiceOwnershipLedgerContract.MaxStringLength));
            Assert.False(ServiceOwnershipLedgerContract.IsValidKeyIdentity(storeOnly));
        }
    }

    // =======================================================================
    // HELPERS
    // =======================================================================

    private static ServiceOwnershipPromotedRecipeContent Content(string json)
    {
        Assert.True(ServiceOwnershipPromotedRecipeContent.TryCreate(
            Utf8(json), out ServiceOwnershipPromotedRecipeContent content));
        return content;
    }

    private static byte[] Utf8(string text) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

    private static string UpperHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            sb.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

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

    private static string StripCommentLines(string source)
    {
        var sb = new StringBuilder(source.Length);
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

    /// <summary>
    /// A fresh OS-temp containment root, deleted on dispose. This is the ONLY place
    /// a root is supplied, and it is never a product location.
    /// </summary>
    private sealed class Sandbox : IDisposable
    {
        internal Sandbox()
        {
            Root = Path.Combine(Path.GetTempPath(), "paxcookbook-cycle82-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Store = new ServiceOwnershipPromotedRecipeStore(Root);
        }

        internal string Root { get; }

        internal ServiceOwnershipPromotedRecipeStore Store { get; }

        internal string StoreDirectory => Path.Combine(
            Root,
            ServiceMachineStorageContract.MachineRootFolderName,
            ServiceMachineStorageContract.ServiceDataFolderName,
            ServiceMachineStorageContract.MachineRecipeStoreFolderName);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover OS-temp directory is harmless and never a product path.
            }
        }
    }
}
