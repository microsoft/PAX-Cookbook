using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace PAXCookbook.App.Tests;

// Cycle 35 - RETIREMENT of the obsolete X15 no-child cook-preparation seam.
//
// SCOPE: every supported desktop Cook execution entry point now traverses
// CookPreparationSequence and the shared SpawnAndSupervise core. The old
// --test-seam-cook-prepare flag no longer prepares anything; a stale invocation
// must FAIL CLOSED before any product-owned startup work happens.
//
// Nothing here launches a process, runs PAX, starts a Bake, reads engine bytes,
// or reads a secret. Every scan is comment-stripped, token-precise, and paired
// with a POSITIVE CONTROL proving the scanner can actually fire.
public class RetiredCookPrepareSeamTests
{
    private const string ProgramRel = "src/PAXCookbook.App/Program.cs";

    // The call site of the retirement classifier, as it must appear inside Main.
    private const string RefusalCallToken = "IsRetiredCookPrepareSeamRequested(args)";
    private const string MainAnchor = "public static int Main(string[] args)";

    // ---- structural: the refusal is the first thing Main does ---------------

    [Fact]
    public void Retired_seam_refusal_precedes_isolation_activation_and_startup_log()
    {
        string main = MainBody();

        int refusal = main.IndexOf(RefusalCallToken, StringComparison.Ordinal);
        Assert.True(
            refusal >= 0,
            "Main does not refuse the retired cook-preparation seam at all.");

        int isolation = main.IndexOf("TestIsolationRuntime.TryActivate", StringComparison.Ordinal);
        int startupLog = main.IndexOf("StartupLog.Begin", StringComparison.Ordinal);
        Assert.True(isolation >= 0, "TestIsolationRuntime.TryActivate not found in Main.");
        Assert.True(startupLog >= 0, "StartupLog.Begin not found in Main.");

        Assert.True(refusal < isolation, "Refusal does not precede isolation activation.");
        Assert.True(refusal < startupLog, "Refusal does not precede StartupLog initialization.");

        // It is the FIRST statement: nothing product-owned runs before it.
        Assert.True(
            refusal < main.IndexOf("SetCurrentProcessExplicitAppUserModelID", StringComparison.Ordinal),
            "Refusal does not precede SetCurrentProcessExplicitAppUserModelID.");
        Assert.True(
            refusal < main.IndexOf("_interactiveLaunch", StringComparison.Ordinal),
            "Refusal does not precede the _interactiveLaunch capture.");
        Assert.True(
            refusal < main.IndexOf("ProviderNativeTestMode", StringComparison.Ordinal),
            "Refusal does not precede ProviderNativeTestMode.");
    }

    [Fact]
    public void The_precedence_scanner_can_fire()
    {
        // POSITIVE CONTROL for the scan above. The same ordering logic applied to
        // a synthetic Main whose refusal sits AFTER isolation activation reports a
        // violation, so the assertions above are real findings and not vacuous.
        const string bad = @"
public static int Main(string[] args)
{
    int? iso = TestIsolationRuntime.TryActivate(args);
    StartupLog.Begin(args);
    if (IsRetiredCookPrepareSeamRequested(args)) { return 80; }
    return 0;
}";
        string body = ExtractMethodRegion(StripComments(bad), MainAnchor);
        int refusal = body.IndexOf(RefusalCallToken, StringComparison.Ordinal);
        int isolation = body.IndexOf("TestIsolationRuntime.TryActivate", StringComparison.Ordinal);
        int startupLog = body.IndexOf("StartupLog.Begin", StringComparison.Ordinal);

        Assert.True(refusal >= 0);
        Assert.True(isolation >= 0);
        Assert.True(startupLog >= 0);
        Assert.False(refusal < isolation, "control: refusal wrongly reported as early");
        Assert.False(refusal < startupLog, "control: refusal wrongly reported as early");

        // And the comment stripper really blinds the scan to a commented-out
        // refusal, so a comment can never satisfy the precedence assertion.
        const string commented = @"
public static int Main(string[] args)
{
    // IsRetiredCookPrepareSeamRequested(args)
    int? iso = TestIsolationRuntime.TryActivate(args);
    StartupLog.Begin(args);
    return 0;
}";
        string strippedBody = ExtractMethodRegion(StripComments(commented), MainAnchor);
        Assert.DoesNotContain(RefusalCallToken, strippedBody, StringComparison.Ordinal);
        Assert.Contains(RefusalCallToken, commented, StringComparison.Ordinal);
    }

    // ---- in-process: the classifier and the bounded refusal -----------------

    [Fact]
    public void The_exact_retired_flag_is_classified()
    {
        Assert.True(Program.IsRetiredCookPrepareSeamRequested(
            new[] { "--test-seam-cook-prepare" }));

        // Classified even when it arrives alongside other arguments.
        Assert.True(Program.IsRetiredCookPrepareSeamRequested(
            new[] { "--headless", "--test-seam-cook-prepare", "--no-window" }));

        // Nothing to classify is not a refusal.
        Assert.False(Program.IsRetiredCookPrepareSeamRequested(Array.Empty<string>()));
        Assert.False(Program.IsRetiredCookPrepareSeamRequested(new[] { "--headless" }));
    }

    [Theory]
    [InlineData("--test-seam-cook-prepare")]
    [InlineData("--TEST-SEAM-COOK-PREPARE")]
    [InlineData("--Test-Seam-Cook-Prepare")]
    [InlineData("--tEsT-sEaM-cOoK-pRePaRe")]
    [InlineData("--TEST-seam-COOK-prepare")]
    public void Every_case_variant_is_classified_and_refused(string variant)
    {
        // Ordinal-ignore-case EXACT equality: an uppercase form must NOT evade
        // detection and slip through into a full startup.
        Assert.True(Program.IsRetiredCookPrepareSeamRequested(new[] { variant }));
    }

    [Theory]
    [InlineData("--test-seam-cook-preparex")]
    [InlineData("x--test-seam-cook-prepare")]
    [InlineData("--test-seam-cook-prep")]
    [InlineData("--test-seam-cook-prepare=1")]
    [InlineData("--test-seam-cook-prepare-gate")]
    [InlineData("test-seam-cook-prepare")]
    [InlineData("--test-seam-cook-prepare ")]
    [InlineData(" --test-seam-cook-prepare")]
    [InlineData("--test-seam-cook-preparation")]
    public void Prefix_suffix_and_substring_lookalikes_are_not_classified(string lookalike)
    {
        // Only EXACT equality classifies. A lookalike is not the retired flag and
        // is left to normal argument handling rather than being refused.
        Assert.False(Program.IsRetiredCookPrepareSeamRequested(new[] { lookalike }));
    }

    [Fact]
    public void The_retirement_exit_code_and_token_are_exact_and_bounded()
    {
        Assert.Equal(80, Program.RetiredCookPrepareSeamExit);
        Assert.Equal(
            "COOK_PREPARE_SEAM_RETIRED=--test-seam-cook-prepare",
            Program.RetiredCookPrepareSeamToken);

        // The token carries no path, workspace, user, engine, exception, or other
        // dynamic state: it is a compile-time constant with no interpolation and
        // no separator that could introduce a second line.
        Assert.DoesNotContain("\n", Program.RetiredCookPrepareSeamToken, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", Program.RetiredCookPrepareSeamToken, StringComparison.Ordinal);

        // 80 is distinct from every exit code the App already uses, and in
        // particular from the two test-infrastructure refusals it sits above.
        Assert.NotEqual(78, Program.RetiredCookPrepareSeamExit);
        Assert.NotEqual(79, Program.RetiredCookPrepareSeamExit);
        Assert.NotEqual(87, Program.RetiredCookPrepareSeamExit);
    }

    [Fact]
    public void The_refusal_path_emits_exactly_one_bounded_line_and_nothing_else()
    {
        string main = MainBody();
        int start = main.IndexOf("if (" + RefusalCallToken + ")", StringComparison.Ordinal);
        Assert.True(start >= 0, "refusal block not found in Main");

        int open = main.IndexOf('{', start);
        int depth = 0;
        int end = -1;
        for (int i = open; i < main.Length; i++)
        {
            if (main[i] == '{') depth++;
            else if (main[i] == '}')
            {
                depth--;
                if (depth == 0) { end = i; break; }
            }
        }
        Assert.True(end > open);

        string block = Normalize(main[start..(end + 1)]);
        Assert.Equal(
            "if (IsRetiredCookPrepareSeamRequested(args)) { "
            + "Console.WriteLine(RetiredCookPrepareSeamToken); "
            + "return RetiredCookPrepareSeamExit; }",
            block);
    }

    // ---- structural: the cook-start route has exactly one path --------------

    [Fact]
    public void CookStartHandler_has_exactly_one_path_and_calls_StartManualCook()
    {
        string handler = ExtractMethodRegion(
            StripComments(ReadSource(ProgramRel)),
            "IResult CookStartHandler(HttpContext ctx, string id)");

        Assert.Contains("RecipeReadModel.StartManualCook(", handler, StringComparison.Ordinal);

        // Exactly ONE read-model call and exactly ONE return: no branch, no
        // second preparation-only path, no alternative response.
        int modelCalls = Regex.Matches(handler, @"RecipeReadModel\.\w+\(").Count;
        int returns = Regex.Matches(handler, @"\breturn\b").Count;
        int ifs = Regex.Matches(handler, @"\bif\s*\(").Count;
        int elses = Regex.Matches(handler, @"\belse\b").Count;
        int switches = Regex.Matches(handler, @"\bswitch\s*\(").Count;
        Assert.Equal(1, modelCalls);
        Assert.Equal(1, returns);
        Assert.Equal(0, ifs);
        Assert.Equal(0, elses);
        Assert.Equal(0, switches);

        // POSITIVE CONTROL: the same counters applied to the PRE-CYCLE-35 shape of
        // this handler report the branch and the second read-model call, so the
        // zero-counts above are real findings. The retired identifiers are spliced
        // in from RetiredTokens so this control is not itself a live reference.
        string preCycle35 =
            "IResult CookStartHandler(HttpContext ctx, string id)\n"
            + "{\n"
            + "    if (" + RetiredTokens[1] + ")\n"
            + "    {\n"
            + "        (int prepStatus, object prepBody) = RecipeReadModel." + RetiredTokens[0] + "(a, b);\n"
            + "        return Results.Json(prepBody, statusCode: prepStatus);\n"
            + "    }\n"
            + "    (int status, object body) = RecipeReadModel.StartManualCook(a, b);\n"
            + "    return Results.Json(body, statusCode: status);\n"
            + "}";
        string control = ExtractMethodRegion(
            StripComments(preCycle35), "IResult CookStartHandler(HttpContext ctx, string id)");
        int controlModelCalls = Regex.Matches(control, @"RecipeReadModel\.\w+\(").Count;
        int controlReturns = Regex.Matches(control, @"\breturn\b").Count;
        int controlIfs = Regex.Matches(control, @"\bif\s*\(").Count;
        Assert.Equal(2, controlModelCalls);
        Assert.Equal(2, controlReturns);
        Assert.Equal(1, controlIfs);
    }

    // ---- structural: zero active references to the retired seam --------------

    // Assembled from fragments ON PURPOSE so this file's own forbidden-token list
    // is not itself a literal occurrence of the tokens it forbids. The scan is
    // comment-stripped, so the explanatory comments naming PrepareCookStart in the
    // sequence files are correctly ignored.
    private static readonly string[] RetiredTokens =
    {
        "Prepare" + "CookStart",
        "cook" + "PrepareSeam",
        "cook_prepared" + "_no_child",
        "cook_child_not" + "_implemented_x15",
        "X15_TEST_SEAM" + "_COOK_PREPARE",
    };

    [Fact]
    public void Zero_active_references_to_the_retired_seam_remain_in_src_and_tests()
    {
        // Sealed _temp cycle history and the append-only backup log are DELIBERATELY
        // out of scope: they are immutable records of what the code used to be.
        var offenders = new List<string>();
        foreach (string file in ProductionAndTestSources())
        {
            string code = StripComments(File.ReadAllText(file));
            foreach (string token in RetiredTokens)
            {
                if (code.Contains(token, StringComparison.Ordinal))
                {
                    offenders.Add(Rel(file) + " :: " + token);
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_retired_reference_scanner_can_fire()
    {
        // POSITIVE CONTROL 1 - a token in CODE position is detected, while the same
        // token in a comment is correctly ignored.
        string codePosition = "var x = \"" + RetiredTokens[2] + "\";";
        string commentPosition = "// " + RetiredTokens[2];
        Assert.Contains(RetiredTokens[2], StripComments(codePosition), StringComparison.Ordinal);
        Assert.DoesNotContain(RetiredTokens[2], StripComments(commentPosition), StringComparison.Ordinal);

        // POSITIVE CONTROL 2 - the same file enumeration really does traverse the
        // real tree and really does find a token that IS present in it.
        string[] files = ProductionAndTestSources();
        Assert.True(files.Length > 50, "source enumeration collapsed to " + files.Length + " files");
        Assert.Contains(
            files,
            f => StripComments(File.ReadAllText(f)).Contains("StartManualCook", StringComparison.Ordinal));

        // POSITIVE CONTROL 3 - every forbidden token is a real, non-empty needle
        // (the fragment concatenation did not silently produce an empty string).
        Assert.All(RetiredTokens, t => Assert.True(t.Length > 8, t));

        // POSITIVE CONTROL 4 - the stripper does not open a PHANTOM block comment
        // from a route glob inside a line comment. This is the exact defect that
        // made an earlier version of this scan swallow ~1700 lines of Program.cs
        // and report a false PASS.
        Assert.Equal(
            "keep",
            StripComments("keep// every other /api/v1/* route\n").Trim());
        Assert.Contains(
            "survives",
            StripComments("// /api/v1/* route\nsurvives\ncatch { /* ignore */ }\n"),
            StringComparison.Ordinal);

        // ... and it still removes a genuine MULTI-LINE block comment.
        Assert.Equal("a c", Normalize(StripComments("a /* b1\nb2\nb3 */ c")));
        Assert.Equal("x z", Normalize(StripComments("x /* y */ z")));

        // ... and the real Program.cs survives stripping largely intact, so a
        // runaway strip can never masquerade as "token absent".
        string rawProgram = ReadSource(ProgramRel);
        string strippedProgram = StripComments(rawProgram);
        Assert.True(
            strippedProgram.Length > rawProgram.Length / 2,
            $"stripper collapsed Program.cs from {rawProgram.Length} to {strippedProgram.Length}");
    }

    // ---- guard against over-deletion ----------------------------------------

    [Fact]
    public void The_still_live_cook_preparation_gate_diagnostic_is_retained()
    {
        string program = StripComments(ReadSource(ProgramRel));

        // X15_COOK_PREPARE_GATE names the still-live PREPARATION GATE CHAIN, not
        // the retired seam. Its historical X15 naming is now misleading but it is
        // deliberately unchanged, so this cycle cannot be read as having deleted it.
        Assert.Contains("X15_COOK_PREPARE_GATE=on", program, StringComparison.Ordinal);
        Assert.Contains("X15_TEST_SEAM_COOK_MIN_FREE_BYTES=", program, StringComparison.Ordinal);
        Assert.Contains("X16_TEST_SEAM_COOK_PWSH_PATH_OVERRIDE=", program, StringComparison.Ordinal);
        Assert.Contains("cookMinFreeBytes", program, StringComparison.Ordinal);
        Assert.Contains("cookPwshPathOverride", program, StringComparison.Ordinal);

        // The preparation helpers the retired seam used to share are still here and
        // still called by the sequenced pipeline.
        string cookStart = StripComments(ReadSource(CookStartRel));
        Assert.Contains("EvaluateCookGatesThroughBusy(", cookStart, StringComparison.Ordinal);
        Assert.Contains("PrepareCookArtifacts(", cookStart, StringComparison.Ordinal);

        string supervisor = StripComments(ReadSource(SupervisorRel));
        Assert.Contains("EvaluateCookGatesThroughBusy(", supervisor, StringComparison.Ordinal);
        Assert.Contains("PrepareCookArtifacts(", supervisor, StringComparison.Ordinal);
    }

    // ---- routing: every supported production trigger is sequenced ------------

    [Fact]
    public void All_three_production_triggers_still_traverse_the_preparation_sequence()
    {
        string supervisor = StripComments(ReadSource(SupervisorRel));
        string resume = StripComments(ReadSource(ResumeRel));

        // Manual and Scheduled both funnel into StartCookCore ...
        foreach (string entry in new[]
                 { "StartManualCook", "StartScheduledCook", "StartScheduledCookViaHttp" })
        {
            string body = ExtractMethodRegion(
                supervisor, "public static (int Status, object Body) " + entry + "(");
            Assert.Contains("StartCookCore(", body, StringComparison.Ordinal);
        }

        // ... and StartCookCore is what runs the sequence.
        string core = ExtractMethodRegion(
            supervisor, "private static (int Status, object Body) StartCookCore(");
        Assert.Contains("CookPreparationSequence.Execute(", core, StringComparison.Ordinal);
        Assert.Contains("MapCookKindToTrigger(kind)", core, StringComparison.Ordinal);
        Assert.Contains("SpawnAndSupervise(", core, StringComparison.Ordinal);

        // Resume runs the same phase order under its own trigger and its own
        // adapter, and reaches the same shared spawn core.
        string resumeBody = ExtractMethodRegion(
            resume, "public static (int Status, object Body) StartResumeCook(");
        Assert.Contains("CookPreparationSequence.Execute(", resumeBody, StringComparison.Ordinal);
        Assert.Contains("CookTriggerKind.Resume", resumeBody, StringComparison.Ordinal);
        Assert.Contains("SpawnAndSupervise(", resumeBody, StringComparison.Ordinal);

        // The CookKind -> trigger mapping is still exhaustive over both kinds.
        Assert.Contains("CookKind.Manual => CookTriggerKind.Manual", supervisor, StringComparison.Ordinal);
        Assert.Contains("CookKind.Scheduled => CookTriggerKind.Scheduled", supervisor, StringComparison.Ordinal);
    }

    // ---- no private visibility was widened -----------------------------------

    [Fact]
    public void No_private_visibility_was_widened()
    {
        // Every protected region keeps EXACTLY the access modifier it had before
        // this cycle. Retiring a seam must not open a private helper to the world.
        var expected = new (string Rel, string Declaration)[]
        {
            (SupervisorRel, "private static (int Status, object Body) StartCookCore("),
            (SupervisorRel, "private static (int Status, object Body) SpawnAndSupervise("),
            (CookStartRel, "private static (int? Status, object? Body, PreparedCook Prepared) PrepareCookArtifacts("),
            (CookStartRel, "private static CookGateOutcome EvaluateCookGatesThroughBusy("),
            (CookStartRel, "private static RecipeCookReservation ReserveRecipeCookRowCore("),
            (ResumeRel, "private static string? ReserveResumeCookRowCore("),
            (ResumeRel, "private static PaxAdapter.InvocationPlan BuildResumeInvocationPlan("),
            (ResumeRel, "private static (List<string> Argv, string Command) BuildResumeArgvAndCommand("),
        };

        foreach ((string rel, string declaration) in expected)
        {
            Assert.Contains(declaration, ReadSource(rel), StringComparison.Ordinal);
        }

        // The shared contract entry point was already public and is unchanged.
        Assert.Contains(
            "public static CookPreparationOutcome Execute(",
            ReadSource("src/PAXCookbook.Shared/Contracts/CookPreparationSequence.cs"),
            StringComparison.Ordinal);

        // The App's own private startup state is still private.
        Assert.Contains("private static bool _interactiveLaunch;", ReadSource(ProgramRel), StringComparison.Ordinal);

        // POSITIVE CONTROL: a widened form of each declaration is genuinely absent,
        // and the same search finds the widened form in a synthetic sample.
        foreach ((string rel, string declaration) in expected)
        {
            string widened = declaration.Replace("private static", "public static", StringComparison.Ordinal);
            Assert.DoesNotContain(widened, ReadSource(rel), StringComparison.Ordinal);
            Assert.Contains(widened, "prefix " + widened + " suffix", StringComparison.Ordinal);
        }
    }

    // ---- helpers ------------------------------------------------------------

    private const string CookStartRel = "src/PAXCookbook.App/RecipeReadModel.CookStart.cs";
    private const string SupervisorRel = "src/PAXCookbook.App/RecipeReadModel.CookSupervisor.cs";
    private const string ResumeRel = "src/PAXCookbook.App/RecipeReadModel.ResumeCook.cs";

    private static string Normalize(string s)
        => Regex.Replace(s.Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim();

    private static string Rel(string fullPath)
        => fullPath.Substring(RepoRoot().Length + 1).Replace('\\', '/');

    // Every authored C# source under src/ and tests/, excluding build output.
    private static string[] ProductionAndTestSources()
    {
        string root = RepoRoot();
        var files = new List<string>();
        foreach (string sub in new[] { "src", "tests" })
        {
            files.AddRange(Directory.EnumerateFiles(
                Path.Combine(root, sub), "*.cs", SearchOption.AllDirectories));
        }
        return files
            .Where(f =>
            {
                string r = Rel(f);
                return !r.Contains("/bin/", StringComparison.Ordinal)
                    && !r.Contains("/obj/", StringComparison.Ordinal);
            })
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
    }

    internal static string RepoRoot([CallerFilePath] string thisFile = "")

    {
        DirectoryInfo? dir = new(Path.GetDirectoryName(thisFile)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PAXCookbook.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    internal static string ReadSource(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    // Comment stripper. Deliberately a left-to-right state machine rather than a
    // /\*.*?\*/ regex: Program.cs contains LINE comments that mention route globs
    // like /api/v1/*, and a regex would treat that as an opening block comment and
    // silently swallow ~1700 lines of real code up to the next */ - which is a
    // FALSE PASS generator for every "token is absent" assertion. A line comment
    // that starts before a /* on the same line wins, so no phantom block ever opens.
    internal static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        bool inBlock = false;

        foreach (string line in source.Split('\n'))
        {
            int i = 0;
            while (i < line.Length)
            {
                if (inBlock)
                {
                    int close = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (close < 0) { i = line.Length; }
                    else { inBlock = false; i = close + 2; }
                    continue;
                }

                int lineComment = line.IndexOf("//", i, StringComparison.Ordinal);
                int blockOpen = line.IndexOf("/*", i, StringComparison.Ordinal);

                if (lineComment >= 0 && (blockOpen < 0 || lineComment < blockOpen))
                {
                    sb.Append(line, i, lineComment - i);
                    i = line.Length;
                }
                else if (blockOpen >= 0)
                {
                    sb.Append(line, i, blockOpen - i);
                    inBlock = true;
                    i = blockOpen + 2;
                }
                else
                {
                    sb.Append(line, i, line.Length - i);
                    i = line.Length;
                }
            }
            sb.Append('\n');
        }

        return sb.ToString();
    }

    // Reproducible method-boundary extraction: from the unique declaration anchor
    // through the matching close brace of the method body. Never a line number.
    internal static string ExtractMethodRegion(string source, string anchor)
    {
        int start = source.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, "anchor not found: " + anchor);
        Assert.True(
            source.IndexOf(anchor, start + 1, StringComparison.Ordinal) < 0,
            "anchor is not unique: " + anchor);

        int open = source.IndexOf('{', start);
        Assert.True(open >= 0, "no opening brace after anchor: " + anchor);

        int depth = 0;
        int end = -1;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) { end = i; break; }
            }
        }
        Assert.True(end > open, "unbalanced braces for anchor: " + anchor);
        return source[start..(end + 1)];
    }

    private static string MainBody()
        => ExtractMethodRegion(StripComments(ReadSource(ProgramRel)), MainAnchor);
}
