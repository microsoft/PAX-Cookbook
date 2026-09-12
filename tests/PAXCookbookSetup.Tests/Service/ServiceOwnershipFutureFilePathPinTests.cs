using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 86 (F-C1) - EXTERNAL PATH PINS FOR THE TWO FUTURE FILES
// ===========================================================================
//
// THE GAP THIS CLOSES. Cycle 85 authorized one file location for the ledger
// WRITER token and one for the promotion EXECUTOR. Both were pinned only
// SELF-REFERENTIALLY: the guard compared its own allowance table to itself, so
// cardinality could not silently widen, but the pair could silently RELOCATE -
// the table and every self-check moving together in one coordinated edit.
//
// WHY THIS FILE IS SEPARATE, AND WHY IT USES LITERALS. It lives OUTSIDE
// ServiceOwnershipRightsProfileBindingTests.cs, in a different test project, and
// it declares its own INDEPENDENT LITERAL expected paths. It DELIBERATELY does
// NOT call the cycle-85 allowance helper, because a pin derived from the thing
// it is pinning is not a pin at all.
//
// WHAT THIS FILE DOES NOT DO. It authorizes nothing and adds no production type.
//
// CYCLE 88 UPDATE. Both files now EXIST, so the two absence assertions became
// EXACT EXISTENCE assertions at the exact pinned paths, plus a negative half
// proving no near-miss or relocated copy exists. Every relocation, swap and
// second-path mutation control is retained unchanged.
public sealed class ServiceOwnershipFutureFilePathPinTests
{
    // ---- THE INDEPENDENT PINS ------------------------------------------------

    private const string LedgerWriterPath =
        "src/PAXCookbookSetup/Service/ServiceOwnershipLedgerWriter.cs";

    private const string PromotionExecutorPath =
        "src/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutor.cs";

    private const string SetupServiceDirectory = "src/PAXCookbookSetup/Service/";

    // The two guards these pins hold to account.
    private const string LedgerTokenGuardPath =
        "tests/PAXCookbook.Shared.Tests/ServiceOwnershipRightsProfileBindingTests.cs";

    private const string ResolverCallGuardPath =
        "tests/PAXCookbookSetup.Tests/Service/ServiceSidResolverTests.cs";

    // ---- the extraction rule, used by the real checks AND every mutation ----

    private static readonly Regex AnySegmentArray = new(
        @"new\[\]\s*\{\s*(""[^""]*""(?:\s*,\s*""[^""]*"")*)\s*,?\s*\}",
        RegexOptions.CultureInvariant);

    private static readonly Regex QuotedSegment = new(@"""([^""]*)""", RegexOptions.CultureInvariant);

    /// <summary>
    /// Every repo-rooted segment array a guard declares, rendered as a forward
    /// slash relative path. An array whose first segment is not "src" is not a
    /// production-source authorization and is ignored.
    /// </summary>
    private static List<string> SrcRootedSegmentPaths(string guardSource)
    {
        var found = new List<string>();
        foreach (Match match in AnySegmentArray.Matches(guardSource))
        {
            List<string> segments = QuotedSegment
                .Matches(match.Groups[1].Value)
                .Select(m => m.Groups[1].Value)
                .ToList();

            if (segments.Count > 1 && string.Equals(segments[0], "src", StringComparison.Ordinal))
            {
                found.Add(string.Join("/", segments));
            }
        }
        return found;
    }

    /// <summary>
    /// THE RULE. A guard authorizes <paramref name="expectedPath"/> correctly only
    /// when it declares that exact path EXACTLY ONCE and declares no OTHER
    /// production path whose leaf carries the same role word.
    /// </summary>
    private static bool GuardAuthorizesExactlyOnePathFor(
        string guardSource, string expectedPath, string roleWord)
    {
        List<string> declared = SrcRootedSegmentPaths(guardSource);

        if (declared.Count(p => string.Equals(p, expectedPath, StringComparison.Ordinal)) != 1)
        {
            return false;
        }

        foreach (string path in declared)
        {
            string leaf = path.Substring(path.LastIndexOf('/') + 1);
            if (leaf.Contains(roleWord, StringComparison.Ordinal)
                && !string.Equals(path, expectedPath, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string SegmentArrayLiteral(string relativePath) =>
        "new[] { "
        + string.Join(", ", relativePath.Split('/').Select(segment => "\"" + segment + "\""))
        + " }";

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

    private static string FullPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(RepoRoot(), Path.Combine(relativePath.Split('/'))));

    private static string SourceOf(string relativePath)
    {
        string full = FullPath(relativePath);
        Assert.True(File.Exists(full), "the guard source is missing: " + relativePath);
        return File.ReadAllText(full);
    }

    // ---- the pins themselves -------------------------------------------------

    [Fact]
    public void The_extraction_rule_is_calibrated_in_both_directions()
    {
        // Without this, every "declares exactly one" below could be vacuous.
        string synthetic =
            "var a = " + SegmentArrayLiteral(LedgerWriterPath) + ";"
            + "var b = " + SegmentArrayLiteral(PromotionExecutorPath) + ";"
            + "var c = new[] { \"tools\", \"Something.cs\" };"
            + "var d = new[] { \"NotAPath\", \"AtAll\" };";

        Assert.Equal(
            new[] { LedgerWriterPath, PromotionExecutorPath },
            SrcRootedSegmentPaths(synthetic));

        Assert.Empty(SrcRootedSegmentPaths("var x = new[] { \"tools\", \"Something.cs\" };"));
    }

    [Fact]
    public void The_ledger_token_guard_authorizes_exactly_the_pinned_writer_path()
    {
        string guard = SourceOf(LedgerTokenGuardPath);

        Assert.Contains(SegmentArrayLiteral(LedgerWriterPath), guard, StringComparison.Ordinal);
        Assert.True(GuardAuthorizesExactlyOnePathFor(guard, LedgerWriterPath, "Writer"));
    }

    [Fact]
    public void The_ledger_token_guard_authorizes_exactly_the_pinned_executor_path()
    {
        string guard = SourceOf(LedgerTokenGuardPath);

        Assert.Contains(SegmentArrayLiteral(PromotionExecutorPath), guard, StringComparison.Ordinal);
        Assert.True(GuardAuthorizesExactlyOnePathFor(guard, PromotionExecutorPath, "Executor"));
    }

    [Fact]
    public void The_resolver_call_guard_names_the_exact_pinned_executor_path()
    {
        string guard = SourceOf(ResolverCallGuardPath);

        Assert.Contains(SegmentArrayLiteral(PromotionExecutorPath), guard, StringComparison.Ordinal);
        Assert.True(GuardAuthorizesExactlyOnePathFor(guard, PromotionExecutorPath, "Executor"));

        // The resolver guard authorizes NO writer location at all.
        Assert.DoesNotContain(
            LedgerWriterPath,
            SrcRootedSegmentPaths(guard),
            StringComparer.Ordinal);
    }

    [Fact]
    public void Both_guards_agree_on_the_one_executor_path()
    {
        string ledgerGuard = SourceOf(LedgerTokenGuardPath);
        string resolverGuard = SourceOf(ResolverCallGuardPath);

        string fromLedger = Assert.Single(
            SrcRootedSegmentPaths(ledgerGuard).Where(p => p.Contains("Executor", StringComparison.Ordinal)));
        string fromResolver = Assert.Single(
            SrcRootedSegmentPaths(resolverGuard).Where(p => p.Contains("Executor", StringComparison.Ordinal)));

        Assert.Equal(fromLedger, fromResolver, StringComparer.Ordinal);
        Assert.Equal(PromotionExecutorPath, fromLedger, StringComparer.Ordinal);
    }

    [Fact]
    public void The_two_pinned_paths_are_distinct()
    {
        Assert.NotEqual(LedgerWriterPath, PromotionExecutorPath, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(
            Path.GetFileName(LedgerWriterPath),
            Path.GetFileName(PromotionExecutorPath),
            StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(LedgerWriterPath)]
    [InlineData(PromotionExecutorPath)]
    public void Each_pinned_path_is_a_direct_child_of_the_exact_setup_service_directory(string pinned)
    {
        Assert.StartsWith(SetupServiceDirectory, pinned, StringComparison.Ordinal);

        string leaf = pinned.Substring(SetupServiceDirectory.Length);
        Assert.DoesNotContain("/", leaf, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", leaf, StringComparison.Ordinal);
        Assert.EndsWith(".cs", leaf, StringComparison.Ordinal);

        // The directory really is the Setup service directory that exists today.
        Assert.True(Directory.Exists(FullPath(SetupServiceDirectory.TrimEnd('/'))));
    }

    [Theory]
    [InlineData(LedgerWriterPath)]
    [InlineData(PromotionExecutorPath)]
    public void Each_pinned_file_exists_at_exactly_its_canonical_pinned_path(string pinned)
    {
        // CYCLE 88 - EXACT EXISTENCE REPLACES TEMPORAL ABSENCE. Brian authorized
        // both files, so this pin now requires each of them AT ITS PINNED PATH.
        // Relocation therefore fails here as well as in the guards themselves.
        string full = FullPath(pinned);

        Assert.True(File.Exists(full), pinned + " must exist at its pinned path");

        // The resolved path is canonical and really is the pinned relative path.
        Assert.Equal(full, Path.GetFullPath(full), StringComparer.Ordinal);
        Assert.Equal(
            Path.GetFileName(Path.Combine(pinned.Split('/'))),
            Path.GetFileName(full),
            StringComparer.Ordinal);
        Assert.Equal(
            FullPath(SetupServiceDirectory.TrimEnd('/')),
            Path.GetDirectoryName(full),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Neither_pinned_file_exists_at_any_near_miss_or_relocated_path()
    {
        // The negative half of the same contract: the exact path exists, and none
        // of the mutation targets does. Without this, "it exists" could be
        // satisfied while a second copy sat somewhere else.
        foreach (string absent in new[]
                 {
                     "src/PAXCookbookSetup/Service/ServiceOwnershipLedgerWriters.cs",
                     "src/PAXCookbookSetup/Service/ServiceOwnershipLedgerWriter2.cs",
                     "src/PAXCookbookSetup/ServiceOwnershipLedgerWriter.cs",
                     "src/PAXCookbook.Shared/Service/ServiceOwnershipLedgerWriter.cs",
                     "src/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutors.cs",
                     "src/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutor2.cs",
                     "src/PAXCookbookSetup/ServiceOwnershipPromotionExecutor.cs",
                     "src/PAXCookbook.App/Service/ServiceOwnershipPromotionExecutor.cs",
                 })
        {
            Assert.False(File.Exists(FullPath(absent)), absent + " must not exist");
        }

        // POSITIVE CONTROL: the same existence check DOES fire on the real files.
        Assert.True(File.Exists(FullPath(LedgerWriterPath)));
        Assert.True(File.Exists(FullPath(PromotionExecutorPath)));
    }

    // ---- mutation controls ---------------------------------------------------
    //
    // Each one feeds a MUTATED path or a MUTATED guard source to the SAME
    // extraction and authorization rules the real checks use, and asserts BOTH
    // directions: the real state passes, the mutated state does not.

    [Theory]
    [InlineData(LedgerWriterPath, "src/PAXCookbookSetup/Service/ServiceOwnershipLedgerWriters.cs")]
    [InlineData(LedgerWriterPath, "src/PAXCookbookSetup/Service/ServiceOwnershipLedgerWrite.cs")]
    [InlineData(PromotionExecutorPath, "src/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutors.cs")]
    [InlineData(PromotionExecutorPath, "src/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutor2.cs")]
    public void MUTATION_a_one_character_filename_change_is_detected(string real, string mutated)
    {
        Assert.NotEqual(real, mutated, StringComparer.Ordinal);

        string guard = SourceOf(LedgerTokenGuardPath);
        Assert.Contains(SegmentArrayLiteral(real), guard, StringComparison.Ordinal);
        Assert.DoesNotContain(SegmentArrayLiteral(mutated), guard, StringComparison.Ordinal);

        // A guard that moved to the mutated name would fail the pin.
        string relocated = guard.Replace(
            SegmentArrayLiteral(real), SegmentArrayLiteral(mutated), StringComparison.Ordinal);
        Assert.False(GuardAuthorizesExactlyOnePathFor(relocated, real, RoleWordFor(real)));
    }

    [Theory]
    [InlineData(LedgerWriterPath, "src/PAXCookbookSetup/ServiceOwnershipLedgerWriter.cs")]
    [InlineData(LedgerWriterPath, "src/PAXCookbook.Shared/Service/ServiceOwnershipLedgerWriter.cs")]
    [InlineData(PromotionExecutorPath, "src/PAXCookbookSetup/ServiceOwnershipPromotionExecutor.cs")]
    [InlineData(PromotionExecutorPath, "src/PAXCookbook.App/Service/ServiceOwnershipPromotionExecutor.cs")]
    public void MUTATION_moving_the_file_to_another_directory_is_detected(string real, string mutated)
    {
        string guard = SourceOf(LedgerTokenGuardPath);
        Assert.DoesNotContain(SegmentArrayLiteral(mutated), guard, StringComparison.Ordinal);

        string relocated = guard.Replace(
            SegmentArrayLiteral(real), SegmentArrayLiteral(mutated), StringComparison.Ordinal);
        Assert.False(GuardAuthorizesExactlyOnePathFor(relocated, real, RoleWordFor(real)));
    }

    [Theory]
    [InlineData(LedgerWriterPath, "src/PAXCookbookSetup/Service/ServiceOwnershipLedgerWriter2.cs")]
    [InlineData(PromotionExecutorPath, "src/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutorAlt.cs")]
    public void MUTATION_a_second_allowed_location_is_detected(string real, string extra)
    {
        string guard = SourceOf(LedgerTokenGuardPath);
        Assert.True(GuardAuthorizesExactlyOnePathFor(guard, real, RoleWordFor(real)));

        string widened = guard + "\nvar extra = " + SegmentArrayLiteral(extra) + ";\n";
        Assert.False(GuardAuthorizesExactlyOnePathFor(widened, real, RoleWordFor(real)));
    }

    [Fact]
    public void MUTATION_a_duplicated_allowance_for_the_same_path_is_detected()
    {
        string guard = SourceOf(LedgerTokenGuardPath);
        string duplicated = guard + "\nvar again = " + SegmentArrayLiteral(LedgerWriterPath) + ";\n";

        Assert.False(GuardAuthorizesExactlyOnePathFor(duplicated, LedgerWriterPath, "Writer"));
    }

    [Fact]
    public void MUTATION_the_two_guards_disagreeing_about_the_executor_is_detected()
    {
        string resolverGuard = SourceOf(ResolverCallGuardPath);
        string divergent = resolverGuard.Replace(
            SegmentArrayLiteral(PromotionExecutorPath),
            SegmentArrayLiteral("src/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutorAlt.cs"),
            StringComparison.Ordinal);

        string fromLedger = Assert.Single(
            SrcRootedSegmentPaths(SourceOf(LedgerTokenGuardPath))
                .Where(p => p.Contains("Executor", StringComparison.Ordinal)));
        string fromDivergent = Assert.Single(
            SrcRootedSegmentPaths(divergent).Where(p => p.Contains("Executor", StringComparison.Ordinal)));

        Assert.NotEqual(fromLedger, fromDivergent, StringComparer.Ordinal);
    }

    [Fact]
    public void MUTATION_swapping_the_writer_and_executor_paths_is_detected()
    {
        string guard = SourceOf(LedgerTokenGuardPath);

        const string writerPlaceholder = "@@WRITER@@";
        string swapped = guard
            .Replace(SegmentArrayLiteral(LedgerWriterPath), writerPlaceholder, StringComparison.Ordinal)
            .Replace(SegmentArrayLiteral(PromotionExecutorPath), SegmentArrayLiteral(LedgerWriterPath), StringComparison.Ordinal)
            .Replace(writerPlaceholder, SegmentArrayLiteral(PromotionExecutorPath), StringComparison.Ordinal);

        // The swap really did change the source...
        Assert.NotEqual(guard, swapped, StringComparer.Ordinal);

        // ...and the ORDER of the declared authorizations is now wrong, which is
        // exactly what a coordinated relocation would look like.
        List<string> real = SrcRootedSegmentPaths(guard);
        List<string> mutated = SrcRootedSegmentPaths(swapped);

        Assert.Equal(real.Count, mutated.Count);
        Assert.False(real.SequenceEqual(mutated, StringComparer.Ordinal));
        Assert.Equal(
            real.IndexOf(LedgerWriterPath),
            mutated.IndexOf(PromotionExecutorPath));
    }

    [Theory]
    [InlineData("tools/PAXCookbookSetup/Service/ServiceOwnershipLedgerWriter.cs")]
    [InlineData("tools/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutor.cs")]
    [InlineData("tests/PAXCookbookSetup/Service/ServiceOwnershipPromotionExecutor.cs")]
    [InlineData("app/PAXCookbookSetup/Service/ServiceOwnershipLedgerWriter.cs")]
    public void MUTATION_a_same_tail_path_outside_src_is_never_an_authorization(string outsideSrc)
    {
        // The extraction rule is anchored at "src", so a same-tail path rooted
        // anywhere else is not even collected as a candidate authorization.
        string synthetic = "var x = " + SegmentArrayLiteral(outsideSrc) + ";";
        Assert.Empty(SrcRootedSegmentPaths(synthetic));

        Assert.DoesNotContain(
            SegmentArrayLiteral(outsideSrc),
            SourceOf(LedgerTokenGuardPath),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            SegmentArrayLiteral(outsideSrc),
            SourceOf(ResolverCallGuardPath),
            StringComparison.Ordinal);
    }

    private static string RoleWordFor(string pinnedPath) =>
        string.Equals(pinnedPath, LedgerWriterPath, StringComparison.Ordinal) ? "Writer" : "Executor";
}
