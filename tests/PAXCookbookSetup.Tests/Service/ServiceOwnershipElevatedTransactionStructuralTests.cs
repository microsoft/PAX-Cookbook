using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 94 (PASS C) - THE ONE AUTHORIZED COMPOSITION ROOT, STRUCTURALLY
// ===========================================================================
//
// WHAT THIS FILE PROVES, AND WHY IT IS SOURCE-SCANNING RATHER THAN BEHAVIOURAL.
// Pass C wires the previously inert promotion surface to a real elevated verb.
// The safety property that wiring must not break is a CONTAINMENT property, not
// a behaviour: every guarded capability - the ledger read, the promoted-Recipe
// store, its fixed port, the promotion executor and its two verbs - must exist
// in EXACTLY ONE production file and NOWHERE ELSE. A behavioural test cannot
// prove "nowhere else"; only a sweep of the authored production sources can.
//
// THE FACADE PROHIBITION IS THE POINT. Brian made it binding that the guarded
// operations must be named DIRECTLY in the one composition root. A wrapper, an
// alias, a reflective lookup, a dynamic invocation, a delegate, a factory hidden
// in an already-exempt file, a partial type, a generated source or a
// token-clean surrogate would each satisfy a naive "exactly one call site"
// count while moving the real capability somewhere no guard is looking. Every
// scan below therefore asserts BOTH that the operation is present in the root
// AND that the escape hatches are absent from it.
//
// EVERY SCANNER IS COMMENT-AND-STRING STRIPPED AND CARRIES BOTH CONTROLS. A raw
// substring scan over raw text is not a guard: it fires on prose in a comment
// and on a literal in a test fixture. Each scan below runs through
// SetupCSharpLexicalScanner and each is calibrated in both directions - it must
// FIND a synthetic occurrence and must NOT find one that exists only in a
// comment.
//
// THIS FILE ADDS NO AUTHORIZATION. It pins what cycle 94's guard authority
// already granted; it never widens it.
public sealed class ServiceOwnershipElevatedTransactionStructuralTests
{
    // ---- THE PINNED PATHS ---------------------------------------------------
    //
    // Declared as forward-slash relative literals rather than repo segment
    // arrays so the external path-pin guard's segment-array extractor still sees
    // exactly the authorizations it already knows about.

    internal const string CompositionRootRelativePath =
        "src/PAXCookbookSetup/Service/ServiceOwnershipElevatedTransaction.cs";

    internal const string ElevatedProtocolRelativePath =
        "src/PAXCookbookSetup/Service/ServiceOwnershipElevatedProtocol.cs";

    internal const string HelperDispatchRelativePath =
        "src/PAXCookbook.ServiceAdminHelper/Enable/ServiceOwnershipElevatedDispatch.cs";

    internal const string HelperProgramRelativePath =
        "src/PAXCookbook.ServiceAdminHelper/Program.cs";

    internal const string HelperProjectRelativePath =
        "src/PAXCookbook.ServiceAdminHelper/PAXCookbook.ServiceAdminHelper.csproj";

    internal const string ChannelRelativePath =
        "src/PAXCookbookSetup/Service/ServiceInitiatingUserIdentityChannel.cs";

    internal static string RepoRoot() => ServiceSidResolverStructuralTests.RepoRoot();

    internal static string FullPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(RepoRoot(), Path.Combine(relativePath.Split('/'))));

    internal static string CompositionRootFullPath() => FullPath(CompositionRootRelativePath);

    private static string RawOf(string relativePath)
    {
        string full = FullPath(relativePath);
        Assert.True(File.Exists(full), "the pinned source is missing: " + relativePath);
        return File.ReadAllText(full);
    }

    private static string CodeOf(string relativePath) =>
        SetupCSharpLexicalScanner.ExtractCode(RawOf(relativePath));

    private static string RootCode() => CodeOf(CompositionRootRelativePath);

    /// <summary>
    /// The authored production sources, bin/obj excluded, ordered so a sweep is
    /// deterministic. This is the SAME set every containment guard in this
    /// project sweeps.
    /// </summary>
    internal static string[] ProductionSources() =>
        Directory
            .GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

    internal static int CountOccurrences(string haystack, string needle)
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
    // 0. THE SCANNER ITSELF IS CALIBRATED IN BOTH DIRECTIONS
    // =======================================================================

    [Fact]
    public void The_scanner_finds_a_real_occurrence_and_never_a_commented_or_quoted_one()
    {
        const string needle = "ServiceOwnershipLedgerReader.Read(";

        // POSITIVE: real code.
        Assert.Equal(
            1,
            CountOccurrences(
                SetupCSharpLexicalScanner.ExtractCode("class X { void M() { var r = " + needle + "); } }"),
                needle));

        // NEGATIVE: the same text in a line comment, a block comment and a string
        // literal must all be invisible.
        Assert.Equal(
            0,
            CountOccurrences(
                SetupCSharpLexicalScanner.ExtractCode("// " + needle + ")\nclass Z { }"),
                needle));
        Assert.Equal(
            0,
            CountOccurrences(
                SetupCSharpLexicalScanner.ExtractCode("/* " + needle + ") */\nclass Z { }"),
                needle));
        Assert.Equal(
            0,
            CountOccurrences(
                SetupCSharpLexicalScanner.ExtractCode("class Z { string s = \"" + needle + ")\"; }"),
                needle));
    }

    // =======================================================================
    // 1. EXACTLY ONE COMPOSITION ROOT, AT EXACTLY THE AUTHORIZED PATH
    // =======================================================================

    [Fact]
    public void Exactly_one_production_file_declares_the_composition_root_and_it_is_the_pinned_file()
    {
        var declaring = new List<string>();
        int calibration = 0;

        foreach (string file in ProductionSources())
        {
            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(file));
            if (code.Contains("ServiceOwnershipLedgerContract", StringComparison.Ordinal))
            {
                calibration++;
            }
            if (code.Contains("class ServiceOwnershipElevatedTransaction", StringComparison.Ordinal))
            {
                declaring.Add(Path.GetFullPath(file));
            }
        }

        Assert.True(calibration > 0, "the positive control failed, so the count below is not calibrated");

        string only = Assert.Single(declaring);
        Assert.Equal(CompositionRootFullPath(), only, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_composition_root_is_not_a_partial_type_and_declares_no_generated_surrogate()
    {
        string code = RootCode();

        Assert.Contains("sealed class ServiceOwnershipElevatedTransaction", code, StringComparison.Ordinal);
        Assert.DoesNotContain("partial ", code, StringComparison.Ordinal);

        // POSITIVE CONTROL: the same scan DOES see the keyword when it is real.
        Assert.Contains(
            "partial ",
            SetupCSharpLexicalScanner.ExtractCode("internal partial class X { }"),
            StringComparison.Ordinal);
    }

    // =======================================================================
    // 2. THE FACADE PROHIBITION - THE SIX DIRECT OPERATIONS, ROOT ONLY
    // =======================================================================
    //
    // Each entry is (operation text, how many times the ROOT must contain it).
    // Every one of them must appear EXACTLY that many times in the root and
    // ZERO times in every other authored production source.

    public static TheoryData<string> DirectRootOperations() => new()
    {
        "ServiceOwnershipLedgerReader.Read(",
        "new ServiceOwnershipPromotedRecipeStore(",
        "new ServiceOwnershipFixedPromotedRecipePort(",
        "new ServiceOwnershipPromotionExecutor(",
        ".Promote(",
        ".ExecutePlan(",
    };

    [Theory]
    [MemberData(nameof(DirectRootOperations))]
    public void Each_guarded_operation_appears_exactly_once_in_the_composition_root(string operation)
    {
        Assert.Equal(1, CountOccurrences(RootCode(), operation));
    }

    [Theory]
    [MemberData(nameof(DirectRootOperations))]
    public void No_other_production_source_contains_a_guarded_operation(string operation)
    {
        var offenders = new List<string>();
        int calibration = 0;

        foreach (string file in ProductionSources())
        {
            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(file));
            if (code.Contains("namespace ", StringComparison.Ordinal))
            {
                calibration++;
            }
            if (string.Equals(
                    Path.GetFullPath(file), CompositionRootFullPath(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (code.Contains(operation, StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file) + ":" + operation);
            }
        }

        Assert.True(calibration > 100, "the positive control failed, so the zero below is not calibrated");
        Assert.Empty(offenders);
    }

    [Theory]
    [MemberData(nameof(DirectRootOperations))]
    public void The_direct_operation_scan_can_actually_fire(string operation)
    {
        // Without this, "exactly once" and "nowhere else" could both be vacuous.
        string synthetic = "class X { void M() { var y = " + operation + "); } }";
        Assert.Equal(1, CountOccurrences(SetupCSharpLexicalScanner.ExtractCode(synthetic), operation));
        Assert.Equal(
            0,
            CountOccurrences(
                SetupCSharpLexicalScanner.ExtractCode("// " + operation + ")\nclass Z { }"),
                operation));
    }

    /// <summary>
    /// THE ESCAPE HATCHES. Every one of these would let a guarded capability be
    /// carried by something other than a direct, named call in the one root, and
    /// every one of them would leave a naive call-site count green.
    /// </summary>
    public static TheoryData<string> ProhibitedIndirectionTokens() => new()
    {
        "Activator.CreateInstance", "Type.GetType", "typeof(", "GetMethod", "GetConstructor",
        "MethodInfo", "MakeGenericMethod", "MakeGenericType", "InvokeMember",
        "System.Reflection", "BindingFlags", "dynamic ", "ExpandoObject",
        "Func<", "Action<", "Delegate", "Lazy<", "Expression<",
    };

    [Theory]
    [MemberData(nameof(ProhibitedIndirectionTokens))]
    public void The_composition_root_carries_no_reflection_dynamic_delegate_or_factory_indirection(string token)
    {
        Assert.DoesNotContain(token, RootCode(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ProhibitedIndirectionTokens))]
    public void The_indirection_scan_can_actually_fire(string token)
    {
        Assert.Contains(
            token,
            SetupCSharpLexicalScanner.ExtractCode("class X { void M() { var y = " + token + " ; } }"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            token,
            SetupCSharpLexicalScanner.ExtractCode("// " + token + "\nclass Z { }"),
            StringComparison.Ordinal);
    }

    // =======================================================================
    // 3. G6 - THE WRITER TOKEN IS NEVER NAMED BY THE ROOT
    // =======================================================================

    [Fact]
    public void The_composition_root_never_names_the_ledger_writer_and_uses_the_fixed_persistence_port_instead()
    {
        string code = RootCode();

        // The guarded writer token must not appear at all - not even once, and
        // not even in a comment, because the raw text is checked too.
        Assert.DoesNotContain("ServiceOwnershipLedgerWriter", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OwnershipLedgerWriter", RawOf(CompositionRootRelativePath), StringComparison.Ordinal);

        // ...and the lawful substitute is constructed exactly once.
        Assert.Equal(1, CountOccurrences(code, "new ServiceOwnershipFixedLedgerPersistencePort("));

        // POSITIVE CONTROL: the writer token IS findable when it is present.
        Assert.Contains(
            "ServiceOwnershipLedgerWriter",
            SetupCSharpLexicalScanner.ExtractCode("class X { void M() { ServiceOwnershipLedgerWriter.Write(); } }"),
            StringComparison.Ordinal);
    }

    // =======================================================================
    // 4. EVERY FIXED ADAPTER IS CONSTRUCTED EXACTLY ONCE, IN THE ROOT ONLY
    // =======================================================================

    public static TheoryData<string> FixedAdapterConstructions() => new()
    {
        "new ServiceOwnershipFixedOwnerIdentityPort(",
        "new ServiceOwnershipFixedServiceSidPort(",
        "new ServiceOwnershipFixedCertificateFactsPort(",
        "new ServiceOwnershipFixedPriorDescriptorPort(",
        "new ServiceOwnershipFixedApprovedDescriptorPort(",
        "new ServiceOwnershipFixedDescriptorRestorePort(",
        "new ServiceOwnershipCertifiedEntryObservationAdapter(",
        "new ServiceOwnershipFixedLedgerPersistencePort(",
    };

    [Theory]
    [MemberData(nameof(FixedAdapterConstructions))]
    public void Each_fixed_adapter_is_constructed_exactly_once_and_only_in_the_composition_root(string construction)
    {
        Assert.Equal(1, CountOccurrences(RootCode(), construction));

        var offenders = new List<string>();
        foreach (string file in ProductionSources())
        {
            if (string.Equals(
                    Path.GetFullPath(file), CompositionRootFullPath(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(file));
            if (code.Contains(construction, StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        Assert.Empty(offenders);
    }

    // =======================================================================
    // 5. THE CLOSED ELEVATED PROTOCOL - EXACTLY TWO VERBS
    // =======================================================================

    [Fact]
    public void The_elevated_protocol_declares_exactly_the_two_ownership_verbs()
    {
        string raw = RawOf(ElevatedProtocolRelativePath);

        Assert.Contains("\"ownership-promote-elevated\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"ownership-depromote-elevated\"", raw, StringComparison.Ordinal);

        // EXACTLY TWO verb literals - a third spelling anywhere in the file fails.
        MatchCollection verbs = Regex.Matches(raw, "\"ownership-[a-z-]+\"", RegexOptions.CultureInvariant);
        var distinct = verbs.Select(m => m.Value).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "\"ownership-depromote-elevated\"", "\"ownership-promote-elevated\"" },
            distinct);
    }

    [Fact]
    public void The_elevated_protocol_carries_only_the_endpoint_pid_and_creation_filetime()
    {
        string code = CodeOf(ElevatedProtocolRelativePath);

        // The three - and only three - values that may cross the boundary are
        // spelled by the EXISTING shared option authority, never retyped here.
        Assert.Contains("ServiceEnableVerbs.EndpointOption", code, StringComparison.Ordinal);
        Assert.Contains("ServiceEnableVerbs.InitiatorProcessIdOption", code, StringComparison.Ordinal);
        Assert.Contains("ServiceEnableVerbs.InitiatorCreatedOption", code, StringComparison.Ordinal);

        // Nothing that would carry authority may be nameable in an argument.
        foreach (string forbidden in new[]
                 {
                     "--path", "--root", "--directory", "--store", "--service", "--account",
                     "--sid", "--thumbprint", "--certificate", "--registry", "--command",
                     "--force", "--purge", "--all", "--override", "--job", "--recipe",
                 })
        {
            Assert.DoesNotContain(forbidden, RawOf(ElevatedProtocolRelativePath), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_verb_selects_the_operation_before_any_payload_is_parsed()
    {
        string rootCode = RootCode();

        // The operation is fixed by the CONSTRUCTOR, so it is decided before a
        // single payload byte exists...
        Assert.Contains(
            "ServiceOwnershipElevatedTransaction(ServiceOwnershipElevatedOperation",
            rootCode,
            StringComparison.Ordinal);

        // ...and exactly ONE parser call site exists for each operation.
        Assert.Equal(1, CountOccurrences(rootCode, "ServicePromotionRequestParser.ParsePromotion("));
        Assert.Equal(1, CountOccurrences(rootCode, "ServicePromotionRequestParser.ParseDepromotion("));

        // THE OPERATION IS NEVER INFERRED FROM THE PAYLOAD. Nothing in the root
        // may look at the document's shape to decide what to do.
        foreach (string sniffing in new[]
                 {
                     "JsonDocument", "JsonElement", "JsonValueKind", "JsonSerializer",
                     "recipeBase64", "certificateThumbprintSha1", "Contains(\"",
                 })
        {
            Assert.DoesNotContain(sniffing, rootCode, StringComparison.Ordinal);
        }
    }

    // =======================================================================
    // 6. THE FIXED RECIPE-STORE ROOT IS DERIVED, NEVER SUPPLIED
    // =======================================================================

    [Fact]
    public void The_store_root_is_derived_once_from_the_fixed_machine_storage_contract_only()
    {
        string code = RootCode();

        Assert.Equal(
            1,
            CountOccurrences(code, "Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)"));

        // No caller path, argv, environment variable, configuration, registry
        // value, callback, factory or override may influence it.
        foreach (string forbidden in new[]
                 {
                     "GetEnvironmentVariable", "Registry", "ConfigurationManager", "AppContext",
                     "args[", "argv", "Directory.SetCurrentDirectory", "Environment.CurrentDirectory",
                     "Path.GetTempPath", "containmentRoot =",
                 })
        {
            Assert.DoesNotContain(forbidden, code, StringComparison.Ordinal);
        }

        // POSITIVE CONTROL for the same scan.
        Assert.Contains(
            "GetEnvironmentVariable",
            SetupCSharpLexicalScanner.ExtractCode("class X { void M() { Environment.GetEnvironmentVariable(\"P\"); } }"),
            StringComparison.Ordinal);
    }

    // =======================================================================
    // 7. THE COMPOSITION ROOT NEVER GAINS AN UNRELATED CAPABILITY
    // =======================================================================

    public static TheoryData<string> ForbiddenRootCapabilities() => new()
    {
        "X509Store", "CngKey", "NCryptOpenKey", "CredRead", "CredWrite", "PasswordVault",
        "OpenSCManager", "CreateService", "DeleteService", "ServiceController",
        "Process.Start", "ProcessStartInfo", "runas", "HttpClient", "WebClient", "Socket",
        "PAXCookbook.App", "web-react", "WebView2", "CoreWebView2",
        "PAX_Purview", "PaxEngine", "StartBake", "startCook",
        "ServiceSidResolver", "ServiceOwnershipCredentialObserver",
    };

    [Theory]
    [MemberData(nameof(ForbiddenRootCapabilities))]
    public void The_composition_root_contains_no_forbidden_capability(string token)
    {
        Assert.DoesNotContain(token, RootCode(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ForbiddenRootCapabilities))]
    public void The_forbidden_root_capability_scan_can_actually_fire(string token)
    {
        Assert.Contains(
            token,
            SetupCSharpLexicalScanner.ExtractCode("class X { void M() { var y = " + token + " ; } }"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            token,
            SetupCSharpLexicalScanner.ExtractCode("// " + token + "\nclass Z { }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_composition_root_reaches_the_lifecycle_planner_exactly_once_through_its_own_qualified_name()
    {
        string code = RootCode();

        // Exactly ONE plan is ever produced, and it is produced by name.
        Assert.Equal(1, CountOccurrences(code, "ServiceOwnershipLifecyclePlanner.Plan("));
        Assert.Equal(1, CountOccurrences(code, "ServiceOwnershipLifecyclePlanRequest.ForUnpublishJob("));

        // ...and no OTHER planner factory is reachable, so a depromotion can
        // never be re-aimed at a different lifecycle operation.
        foreach (string other in new[]
                 {
                     "ForRestoreCredential(", "ForDisableCleanup(", "ForEntryOperation(",
                 })
        {
            Assert.DoesNotContain(other, code, StringComparison.Ordinal);
        }
    }

    // =======================================================================
    // 8. THE HELPER: EXACTLY TWO NEW BRANCHES, NO PROJECT REFERENCE
    // =======================================================================

    [Fact]
    public void The_helper_program_dispatches_exactly_two_ownership_branches()
    {
        string code = CodeOf(HelperProgramRelativePath);

        Assert.Equal(1, CountOccurrences(code, "ServiceOwnershipElevatedVerbs.IsPromoteRequested("));
        Assert.Equal(1, CountOccurrences(code, "ServiceOwnershipElevatedVerbs.IsDepromoteRequested("));

        // EXACTLY TWO ownership dispatch call sites, and both go to the ONE
        // ownership dispatch entry point.
        Assert.Equal(2, CountOccurrences(code, "ServiceOwnershipElevatedDispatch.Run("));
    }

    [Fact]
    public void The_helper_dispatch_binds_never_create_and_the_bounded_self_timeout()
    {
        string code = CodeOf(HelperDispatchRelativePath);

        Assert.Contains("ServiceAnchorCreationPolicy.NeverCreate", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ServiceAnchorCreationPolicy.CreateOrReuse", code, StringComparison.Ordinal);

        // The EXISTING identity-bound pipe and the EXISTING bounded timeout are
        // reused, never re-derived.
        Assert.Contains(
            "ServiceInitiatingUserIdentityChannelServer.TryCreateForBoundInitiator(",
            code,
            StringComparison.Ordinal);
        Assert.Contains("ServiceEnableVerbs.HelperSelfTimeout", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// A project file with every XML COMMENT removed.
    ///
    /// WHY THIS EXISTS. A raw substring scan over a .csproj is not a guard: this
    /// project's header comments legitimately NAME the very dependencies the rule
    /// forbids, in order to state that they are absent. Scanning the raw text
    /// would fire on that prose and would keep firing until somebody deleted the
    /// documentation - which is the exact incentive a guard must never create.
    /// Only real MSBuild markup is scanned.
    /// </summary>
    private static string ProjectMarkupOnly(string projectText) =>
        Regex.Replace(projectText, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    [Fact]
    public void The_project_comment_stripper_is_calibrated_in_both_directions()
    {
        // POSITIVE: real markup survives.
        Assert.Contains(
            "<ProjectReference Include=\"x\" />",
            ProjectMarkupOnly("<Project><ProjectReference Include=\"x\" /></Project>"),
            StringComparison.Ordinal);

        // NEGATIVE: the same text inside a single-line and a multi-line comment
        // is invisible.
        Assert.DoesNotContain(
            "ProjectReference",
            ProjectMarkupOnly("<Project><!-- no ProjectReference here --></Project>"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MSAL",
            ProjectMarkupOnly("<Project><!--\n  no MSAL,\n  no WebView2\n--></Project>"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_helper_project_has_no_project_reference_and_no_desktop_dependency()
    {
        string project = ProjectMarkupOnly(RawOf(HelperProjectRelativePath));

        Assert.DoesNotContain("<ProjectReference", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<PackageReference", project, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("<UseWindowsForms>false</UseWindowsForms>", project, StringComparison.Ordinal);
        Assert.Contains("<UseWPF>false</UseWPF>", project, StringComparison.Ordinal);

        foreach (string forbidden in new[]
                 {
                     "Microsoft.Identity", "MSAL", "WebView2", "PAX_Purview", "CookEngine",
                     "PAXCookbook.App\\", "PAXCookbookSetup\\PAXCookbookSetup.csproj",
                 })
        {
            Assert.DoesNotContain(forbidden, project, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Every_linked_helper_source_is_linked_exactly_once()
    {
        string project = ProjectMarkupOnly(RawOf(HelperProjectRelativePath));

        MatchCollection includes = Regex.Matches(
            project, @"<Compile\s+Include=""([^""]+)""", RegexOptions.CultureInvariant);

        Assert.NotEmpty(includes);

        var seen = new List<string>();
        var duplicates = new List<string>();
        foreach (Match match in includes)
        {
            string value = match.Groups[1].Value;
            if (seen.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                duplicates.Add(value);
            }
            seen.Add(value);
        }

        Assert.Empty(duplicates);

        // The composition root and the elevated protocol are both linked, so the
        // helper genuinely compiles the ONE implementation rather than a copy.
        Assert.Contains(
            seen,
            v => v.EndsWith("ServiceOwnershipElevatedTransaction.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            seen,
            v => v.EndsWith("ServiceOwnershipElevatedProtocol.cs", StringComparison.OrdinalIgnoreCase));

        // ...and every linked path really exists on disk.
        foreach (string value in seen)
        {
            string full = Path.GetFullPath(Path.Combine(
                RepoRoot(), "src", "PAXCookbook.ServiceAdminHelper", value.Replace('\\', Path.DirectorySeparatorChar)));
            Assert.True(File.Exists(full), "a linked helper source does not exist: " + value);
        }
    }

    // =======================================================================
    // 9. THE TRANSPORT CEILING IS NOW A DERIVATION, NOT A RETYPED LITERAL
    // =======================================================================

    [Fact]
    public void The_payload_transport_ceiling_is_derived_from_the_request_contract()
    {
        string code = CodeOf(ChannelRelativePath);

        Assert.Contains(
            "ServicePromotionRequestParser.MaxRequestChars * 4",
            code,
            StringComparison.Ordinal);

        // The retyped literal is gone from the DECLARATION. It survives only in
        // the test project's pin, which is where a cross-check belongs.
        Assert.DoesNotContain("MaxPayloadBytes = 2097152", code, StringComparison.Ordinal);
    }
}
