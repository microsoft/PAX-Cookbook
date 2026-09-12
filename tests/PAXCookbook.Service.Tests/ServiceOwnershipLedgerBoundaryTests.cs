using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Service.Tests;

// Cycle 38a - SERVICE-SIDE boundary coverage for the compile-linked service
// ownership ledger schema contract.
//
// SCOPE, stated plainly. Compile-linking a PURE schema contract proves exactly one
// thing: one source file, one schema, no drift. It does NOT give the service a
// certificate store, a private key handle, an ACL API, a registry handle, a
// credential vault, a service-control channel, an elevation path, a network stack,
// or a ledger reader or writer. This file exists to keep that honest.
//
// THE HOMONYM - CORRECTED, CYCLE 51. ServiceContract.OwnershipLedgerFileName,
// ServiceOwnershipLedgerContract.LedgerFileName and
// ProvisioningContract.LedgerFileName all spell "ownership-ledger.json", but
// that no longer means all three pairings share the same relationship.
// ServiceContract.OwnershipLedgerFileName and ServiceOwnershipLedgerContract
// .LedgerFileName name THE SAME FILE with THE SAME schema and are now, as of
// cycle 51, DELIBERATELY DERIVED - ServiceContract.OwnershipLedgerFileName is
// composed from ServiceMachineStorageContract.OwnershipLedgerFileName, which is
// itself derived from ServiceOwnershipLedgerContract.LedgerFileName. Against
// ProvisioningContract.LedgerFileName (a DIFFERENT feature, a DIFFERENT
// directory, a DIFFERENT schema) the coincidental-homonymy claim remains TRUE
// and is unaffected by this cycle.
//
// Nothing here installs, registers, starts, or contacts a service, touches machine
// state, spawns a process, runs PAX, or starts a Bake.
public class ServiceOwnershipLedgerBoundaryTests
{
    // CORRECTED, CYCLE 51. This pairing (ServiceContract.OwnershipLedgerFileName vs
    // ServiceOwnershipLedgerContract.LedgerFileName) is DELIBERATE SHARED IDENTITY,
    // not coincidental homonymy: both name the SAME file with the SAME schema, and
    // ServiceContract.OwnershipLedgerFileName is now DERIVED from the ledger
    // contract's constant via ServiceMachineStorageContract. The ASSERTED VALUES
    // below never changed - only the stated relationship did. The genuinely
    // coincidental homonym is ServiceOwnershipLedgerContract.LedgerFileName against
    // ProvisioningContract.LedgerFileName (a different feature/directory/schema),
    // which this constant does not describe and which remains unaffected.
    private const string DerivedIdentityMessage =
        "DELIBERATE SHARED IDENTITY, NOT COINCIDENTAL HOMONYMY (corrected, cycle 51). " +
        "ServiceContract.OwnershipLedgerFileName and ServiceOwnershipLedgerContract.LedgerFileName " +
        "name THE SAME FILE with THE SAME schema, and ServiceContract.OwnershipLedgerFileName is " +
        "now DERIVED from it via ServiceMachineStorageContract.OwnershipLedgerFileName.";

    // ---- the exact contract file is compile-linked ---------------------------

    [Fact]
    public void The_ledger_contract_is_compiled_into_the_service_assembly()
    {
        Assert.Equal(
            typeof(ServiceContract).Assembly,
            typeof(ServiceOwnershipLedgerContract).Assembly);

        Assert.Equal(
            "PAXCookbook.Service",
            typeof(ServiceOwnershipLedgerContract).Assembly.GetName().Name);

        // The whole declared surface travels with it, not just the constants type.
        foreach (Type type in new[]
                 {
                     typeof(ServiceOwnershipLedgerValidator),
                     typeof(ServiceOwnershipLedgerValidationResult),
                     typeof(ServiceOwnershipLedgerDocument),
                     typeof(ServiceOwnershipLedgerEntry),
                     typeof(ServiceOwnershipRightsMask),
                     typeof(ServiceOwnershipLedgerOutcome),
                     typeof(ServiceOwnershipLedgerInvalidReason),
                     typeof(ServiceOwnershipCredentialKind),
                     typeof(ServiceOwnershipProvenance),
                     typeof(ServiceOwnershipPrivateKeyProviderKind),
                     typeof(ServiceOwnershipRightsProfileId),
                     typeof(ServiceOwnershipGrantMechanism),
                     typeof(ServiceOwnershipPriorDaclState),
                     typeof(ServiceOwnershipLifecycleState),
                     typeof(ServiceOwnershipTransactionState),
                 })
        {
            Assert.Equal(typeof(ServiceContract).Assembly, type.Assembly);
        }
    }

    [Fact]
    public void The_service_project_links_the_ledger_contract_and_still_adds_no_project_reference()
    {
        string csproj = File.ReadAllText(Path.Combine(
            ServiceCookPreparationBoundaryTests.RepoRoot(),
            "src", "PAXCookbook.Service", "PAXCookbook.Service.csproj"));

        Assert.Contains("ServiceOwnershipLedgerContract.cs", csproj, StringComparison.Ordinal);
        Assert.Contains("CookPreparationSequence.cs", csproj, StringComparison.Ordinal);
        Assert.DoesNotContain("<ProjectReference", csproj, StringComparison.Ordinal);
    }

    [Fact]
    public void The_service_assembly_references_neither_shared_nor_the_app()
    {
        string[] referenced = typeof(ServiceContract).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.NotEmpty(referenced);
        Assert.DoesNotContain("PAXCookbook.Shared", referenced);
        Assert.DoesNotContain("PAXCookbook.App", referenced);
        Assert.DoesNotContain("PAX Cookbook", referenced);
    }

    // ---- homonym pinning, service side ---------------------------------------

    [Fact]
    public void The_service_ledger_leaf_name_is_now_a_deliberately_derived_shared_identity()
    {
        // Cycle 51 doctrine correction. This assertion's VALUE never changed - the
        // two constants have always spelled the same leaf name - but the
        // RELATIONSHIP changed: ServiceContract.OwnershipLedgerFileName is now
        // DERIVED from ServiceOwnershipLedgerContract.LedgerFileName (via the fifth
        // compile-linked ServiceMachineStorageContract) rather than an
        // independently retyped literal that merely happened to match. The
        // ProvisioningContract.LedgerFileName pairing is UNAFFECTED and remains a
        // genuine coincidental homonym - see ServiceOwnershipLedgerContract.cs.
        Assert.True("ownership-ledger.json" == ServiceContract.OwnershipLedgerFileName, DerivedIdentityMessage);
        Assert.True(
            ServiceContract.OwnershipLedgerFileName == ServiceOwnershipLedgerContract.LedgerFileName,
            DerivedIdentityMessage);

        // Same spelling, different ownership identity from ManagedChefKeys.
        Assert.Equal("PAXCookbook.ServiceOwnership.v1", ServiceOwnershipLedgerContract.ProductOwnershipMarker);
        Assert.Equal("service-credential-ownership", ServiceOwnershipLedgerContract.ManagedFeatureId);
    }

    [Fact]
    public void The_fixed_leaf_file_names_are_unchanged_at_five_entries_in_the_same_order()
    {
        Assert.Equal(
            new[]
            {
                "service-status.json",
                "service-heartbeat.json",
                "startup-probe-request.json",
                "startup-probe-result.json",
                "ownership-ledger.json",
            },
            ServiceContract.FixedLeafFileNames.ToArray());

        Assert.Equal(5, ServiceContract.FixedLeafFileNames.Count);
        Assert.Equal("ownership-ledger.json", ServiceContract.FixedLeafFileNames[4]);
    }

    // ---- no runtime reader or writer -----------------------------------------

    private static readonly string[] LedgerTokens =
    {
        "OwnershipLedgerFile",
        "ServiceOwnershipLedgerContract",
        "ServiceOwnershipLedgerValidator",
        "ServiceOwnershipLedgerDocument",
        "ServiceOwnershipLedgerEntry",
    };

    private static readonly string[] FileIoTokens =
    {
        "ReadAllText", "ReadAllBytes", "WriteAllText", "WriteAllBytes", "OpenRead",
        "OpenWrite", "AppendAllText", "StreamReader", "StreamWriter", "FileStream",
        "Delete", "Move", "Replace", "Copy",
    };

    private static bool ReadsOrWritesTheLedger(string source)
    {
        foreach (string line in ServiceCookPreparationBoundaryTests.StripComments(source).Split('\n'))
        {
            bool namesLedger = LedgerTokens.Any(t => line.Contains(t, StringComparison.Ordinal));
            if (!namesLedger)
            {
                continue;
            }
            if (FileIoTokens.Any(t => line.Contains(t, StringComparison.Ordinal)))
            {
                return true;
            }
        }
        return false;
    }

    [Fact]
    public void No_service_source_file_reads_or_writes_the_ownership_ledger()
    {
        string[] offenders = ServiceCookPreparationBoundaryTests.ServiceSourceFiles()
            .Where(f => ReadsOrWritesTheLedger(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_ledger_io_scanner_fires_on_a_synthetic_positive_control()
    {
        // POSITIVE CONTROL. Without this, "0 offenders" above is unfalsifiable.
        Assert.True(ReadsOrWritesTheLedger("var s = File.ReadAllText(paths.OwnershipLedgerFile);"));
        Assert.True(ReadsOrWritesTheLedger("File.WriteAllBytes(paths.OwnershipLedgerFile, bytes);"));
        Assert.True(ReadsOrWritesTheLedger("using var w = new StreamWriter(paths.OwnershipLedgerFile);"));

        // NEGATIVE CONTROL with teeth: pure path composition is NOT a read or write,
        // which is exactly what ServicePaths does today.
        Assert.False(ReadsOrWritesTheLedger(
            "OwnershipLedgerFile = Path.Combine(root, ServiceContract.OwnershipLedgerFileName);"));
    }

    [Fact]
    public void Neither_program_nor_the_startup_probe_worker_mentions_the_ledger()
    {
        foreach (string name in new[] { "Program.cs", "StartupProbeWorker.cs" })
        {
            string body = ServiceCookPreparationBoundaryTests.StripComments(
                File.ReadAllText(Path.Combine(
                    ServiceCookPreparationBoundaryTests.ServiceSourceRoot(), name)));

            foreach (string token in LedgerTokens)
            {
                Assert.DoesNotContain(token, body, StringComparison.Ordinal);
            }
        }

        // POSITIVE CONTROL: the same reader really does see content in those files.
        Assert.Contains(
            "StartupProbeWorker",
            ServiceCookPreparationBoundaryTests.StripComments(File.ReadAllText(Path.Combine(
                ServiceCookPreparationBoundaryTests.ServiceSourceRoot(), "StartupProbeWorker.cs"))),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_linked_contract_is_referenced_by_no_service_source_file()
    {
        // The contract compiles INTO the service, but nothing in the service calls
        // it. There is no ledger reader, no ledger writer and no ledger consumer.
        string[] offenders = ServiceCookPreparationBoundaryTests.ServiceSourceFiles()
            .Where(f => ServiceCookPreparationBoundaryTests.StripComments(File.ReadAllText(f))
                .Contains("ServiceOwnershipLedger", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToArray();

        Assert.Empty(offenders);
    }

    // ---- no new capability entered the service -------------------------------

    public static TheoryData<string> ForbiddenCapabilityTokens() => new()
    {
        // certificate and private key
        "X509", "CngKey", "CngProvider", "CngKeyCreationParameters", "RSACng", "SafeNCryptKeyHandle",
        // ACL and identity
        "AccessControl", "CryptoKeySecurity", "CryptoKeyRights", "FileSecurity", "FileSystemAccessRule",
        "SecurityIdentifier", "WindowsIdentity", "WindowsPrincipal", "RawSecurityDescriptor",
        // registry and credential vault
        "RegistryKey", "Microsoft.Win32.Registry", "CredRead", "CredWrite", "WindowsCredentialStore",
        // service control and elevation
        "ServiceController", "ServiceInstaller", "sc.exe", "runas", "ShellExecute",
        // network
        "HttpClient", "WebClient", "HttpRequestMessage", "Socket", "TcpClient",
        // PAX and Bake
        "PAX_Purview", "PaxEngine", "StartBake", "Start-Bake",
    };

    [Theory]
    [MemberData(nameof(ForbiddenCapabilityTokens))]
    public void The_service_sources_gained_no_new_capability(string token)
    {
        string[] offenders = ServiceCookPreparationBoundaryTests.ServiceSourceFiles()
            .Where(f => Fires(File.ReadAllText(f), token))
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Theory]
    [MemberData(nameof(ForbiddenCapabilityTokens))]
    public void The_capability_scanner_fires_on_a_synthetic_positive_control(string token)
    {
        // POSITIVE CONTROL for EVERY token above. A scanner that can never match is
        // indistinguishable from a clean codebase without this.
        Assert.True(Fires("var offending = " + token + "();", token));
        Assert.False(Fires("// " + token + " mentioned only in a comment", token));
    }

    private static bool Fires(string source, string token) =>
        ServiceCookPreparationBoundaryTests.StripComments(source)
            .Contains(token, StringComparison.Ordinal);

    // ---- the linked contract is inert in this host ---------------------------

    [Fact]
    public void The_linked_validator_still_fails_closed_inside_the_service_host()
    {
        // Linking a portable validator adds no authority: inside the service host it
        // refuses exactly what it refuses in Setup. Cycle 42 approved ONE rights
        // profile, and that still produces no ACTIVE ownership record here.
        Assert.True(ServiceOwnershipLedgerContract.HasApprovedRightsProfile);

        ServiceOwnershipLedgerValidationResult absent = ServiceOwnershipLedgerValidator.ForAbsentLedger();
        Assert.Equal(ServiceOwnershipLedgerOutcome.Absent, absent.Outcome);
        Assert.Null(absent.Document);

        foreach (string? hostile in new string?[] { null, "", "   ", "{}", "[]", "not json" })
        {
            ServiceOwnershipLedgerValidationResult result = ServiceOwnershipLedgerValidator.Validate(hostile);
            Assert.True(result.IsRefused);
            Assert.Null(result.Document);
        }

        // Cycle 38b. The active OUTCOME no longer exists at all, so the service-side
        // guarantee is now proven by REFLECTION over the linked vocabulary rather
        // than by naming a member the compiler would have to resolve.
        Assert.DoesNotContain("ValidActive", Enum.GetNames(typeof(ServiceOwnershipLedgerOutcome)));
        Assert.DoesNotContain("ActiveNotPermitted", Enum.GetNames(typeof(ServiceOwnershipLedgerInvalidReason)));
        Assert.False(Enum.IsDefined(typeof(ServiceOwnershipLedgerOutcome), 2));
    }
}

// ===========================================================================
// CYCLE 38b - STRING-AWARE C# LEXICAL SCANNER (ruling 7)
// ===========================================================================
//
// WHY THIS EXISTS. Every "the service contains no X" claim in this project is only
// as good as the thing that decides what counts as CODE. The previous decider was a
// heuristic: strip /* ... */ with a regex, then cut each line at the first "//".
// That heuristic is wrong in BOTH directions, and both directions matter.
//
//   FALSE NEGATIVE (the dangerous one). A line such as
//       var url = "http://example"; new HttpClient();
//   contains "//" INSIDE a string literal. The heuristic cuts the line there and
//   throws away the REAL HttpClient() call that follows. A genuinely forbidden
//   capability disappears from the scan and the suite reports a clean codebase.
//
//   FALSE POSITIVE. A verbatim string such as @"/* X509Store */" survives the block
//   comment regex and is then reported as certificate code that does not exist.
//
// This scanner is a real lexer instead. It understands normal strings, verbatim
// strings, interpolated strings (INCLUDING their holes, which really are code and
// are deliberately preserved), raw triple-quoted strings, character literals,
// escape sequences, and "//" or "/*" appearing INSIDE any of those. Comment text and
// literal CONTENT are removed; everything else survives.
//
// KNOWN LIMITATION, stated rather than hidden: an interpolation hole inside a RAW
// interpolated string ($"""...{x}...""") has its content stripped along with the
// rest of the raw literal. No such literal exists in the service today, and the
// direction of that error is to under-report, so it is recorded here as debt rather
// than treated as covered.
internal static class CSharpLexicalScanner
{
    /// <summary>
    /// Returns the source with comment text and string/char literal CONTENT removed,
    /// leaving executable code - including interpolation holes - intact.
    /// </summary>
    public static string ExtractCode(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(source.Length);
        int i = 0;
        int n = source.Length;

        while (i < n)
        {
            char c = source[i];

            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                while (i < n && source[i] != '\n')
                {
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }
                i = Math.Min(i + 2, n);
                sb.Append(' ');
                continue;
            }

            if (c == '\'')
            {
                i++;
                while (i < n)
                {
                    if (source[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }
                    if (source[i] == '\'')
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                sb.Append(' ');
                continue;
            }

            if (c == '"' || c == '@' || c == '$')
            {
                int j = i;
                bool verbatim = false;
                bool interpolated = false;
                while (j < n && (source[j] == '@' || source[j] == '$'))
                {
                    if (source[j] == '@') { verbatim = true; } else { interpolated = true; }
                    j++;
                }

                if (j < n && source[j] == '"')
                {
                    int run = 0;
                    while (j + run < n && source[j + run] == '"')
                    {
                        run++;
                    }

                    if (run >= 3)
                    {
                        i = SkipRawString(source, j, run);
                        sb.Append(' ');
                        continue;
                    }

                    sb.Append(' ');
                    i = ScanString(source, j + 1, verbatim, interpolated, sb);
                    continue;
                }

                // '@' or '$' that does NOT open a string: a verbatim identifier such
                // as @class. It is ordinary code.
                sb.Append(c);
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static int SkipRawString(string s, int openIndex, int run)
    {
        int i = openIndex + run;
        while (i < s.Length)
        {
            if (s[i] == '"')
            {
                int c = 0;
                while (i + c < s.Length && s[i + c] == '"')
                {
                    c++;
                }
                if (c >= run)
                {
                    return i + c;
                }
                i += c;
                continue;
            }
            i++;
        }
        return s.Length;
    }

    private static int ScanString(string s, int k, bool verbatim, bool interpolated, StringBuilder sb)
    {
        int n = s.Length;
        while (k < n)
        {
            char ch = s[k];

            if (!verbatim && ch == '\\')
            {
                k += 2;
                continue;
            }

            if (ch == '"')
            {
                if (verbatim && k + 1 < n && s[k + 1] == '"')
                {
                    k += 2;
                    continue;
                }
                return k + 1;
            }

            if (interpolated && ch == '{')
            {
                if (k + 1 < n && s[k + 1] == '{')
                {
                    k += 2;
                    continue;
                }
                k = CopyInterpolationHole(s, k + 1, sb);
                continue;
            }

            if (interpolated && ch == '}' && k + 1 < n && s[k + 1] == '}')
            {
                k += 2;
                continue;
            }

            k++;
        }
        return n;
    }

    // An interpolation hole is CODE and is copied through verbatim. Copying a little
    // too much is the safe direction for a "must not contain" scan.
    private static int CopyInterpolationHole(string s, int k, StringBuilder sb)
    {
        int depth = 1;
        sb.Append(' ');
        while (k < s.Length)
        {
            char ch = s[k];
            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    sb.Append(' ');
                    return k + 1;
                }
            }
            sb.Append(ch);
            k++;
        }
        return s.Length;
    }
}

// ===========================================================================
// CYCLE 38b - SERVICE-SIDE BOUNDARY COVERAGE FOR THE LIFECYCLE PLANNER
// ===========================================================================
//
// WHY THESE TESTS LIVE IN THIS FILE. The cycle authorises SIX files. Removing the
// retired ValidActive outcome forces an edit to this file, and the prompt's sixth
// slot is explicitly "CHANGE or ADD Service boundary tests". Adding a separate
// planner-boundary file WOULD HAVE MADE SEVEN. The binding six-file limit wins over
// the stated file-layout preference, so the new coverage is added here.
//
// SCOPE. Compile-linking a PURE planner proves one thing: one source file, one set
// of decisions, no drift between Setup and the service. It gives the service no
// certificate store, no private key handle, no ACL API, no registry handle, no
// credential vault, no service-control channel, no elevation path, no network stack,
// and no executor. There IS no executor anywhere in this cycle. Nothing here
// installs, registers, starts, or contacts a service, touches machine state, spawns
// a process, runs PAX, or starts a Bake.
public class ServiceOwnershipLifecyclePlannerBoundaryTests
{
    // ---- the exact planner file is compile-linked -----------------------------

    [Fact]
    public void The_exact_planner_file_is_compiled_into_the_service_assembly()
    {
        Assert.Equal(
            typeof(ServiceContract).Assembly,
            typeof(ServiceOwnershipLifecyclePlanner).Assembly);

        Assert.Equal(
            "PAXCookbook.Service",
            typeof(ServiceOwnershipLifecyclePlanner).Assembly.GetName().Name);

        foreach (Type type in new[]
                 {
                     typeof(ServiceOwnershipLifecyclePlanner),
                     typeof(ServiceOwnershipLifecyclePlanRequest),
                     typeof(ServiceOwnershipLifecyclePlan),
                     typeof(ServiceOwnershipPlannerOperation),
                     typeof(ServiceOwnershipCredentialObservation),
                     typeof(ServiceOwnershipJobRelation),
                     typeof(ServiceOwnershipPlanOutcome),
                     typeof(ServiceOwnershipPlanRefusalReason),
                     typeof(ServiceOwnershipPlanAction),
                 })
        {
            Assert.Equal(typeof(ServiceContract).Assembly, type.Assembly);
        }
    }

    [Fact]
    public void The_service_project_links_the_planner_and_still_adds_no_project_reference()
    {
        string csproj = File.ReadAllText(Path.Combine(
            ServiceCookPreparationBoundaryTests.RepoRoot(),
            "src", "PAXCookbook.Service", "PAXCookbook.Service.csproj"));

        Assert.Contains("ServiceOwnershipLifecyclePlanner.cs", csproj, StringComparison.Ordinal);
        Assert.Contains(
            @"..\PAXCookbook.Shared\Contracts\ServiceOwnershipLifecyclePlanner.cs",
            csproj,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<ProjectReference", csproj, StringComparison.Ordinal);

        // The linked path really resolves to a real file, so "linked" is not a claim
        // about a path that does not exist.
        Assert.True(File.Exists(Path.Combine(
            ServiceCookPreparationBoundaryTests.RepoRoot(),
            "src", "PAXCookbook.Shared", "Contracts", "ServiceOwnershipLifecyclePlanner.cs")));
    }

    // ---- no runtime call site -------------------------------------------------

    private static readonly string[] PlannerTokens =
    {
        "ServiceOwnershipLifecyclePlanner",
        "ServiceOwnershipLifecyclePlanRequest",
        "ServiceOwnershipLifecyclePlan",
        "ServiceOwnershipPlanAction",
        "ServiceOwnershipPlannerOperation",
        "ServiceOwnershipCredentialObservation",
        "ServiceOwnershipJobRelation",
    };

    [Fact]
    public void No_service_source_file_contains_a_planner_call_site()
    {
        string[] files = ServiceCookPreparationBoundaryTests.ServiceSourceFiles();

        // The enumeration itself must be non-empty, or "no offenders" is meaningless.
        Assert.NotEmpty(files);
        Assert.True(files.Length >= 5, "expected the authored service sources, found " + files.Length);

        string[] offenders = files
            .Where(f => PlannerTokens.Any(t =>
                CSharpLexicalScanner.ExtractCode(File.ReadAllText(f)).Contains(t, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_four_named_service_types_never_mention_the_planner()
    {
        foreach (string name in new[]
                 {
                     "Program.cs", "StartupProbeWorker.cs",
                     "DisabledCookPreparationAdapter.cs", "ServiceContract.cs",
                 })
        {
            string path = Path.Combine(ServiceCookPreparationBoundaryTests.ServiceSourceRoot(), name);
            Assert.True(File.Exists(path), name + " was expected to exist");

            string code = CSharpLexicalScanner.ExtractCode(File.ReadAllText(path));

            // POSITIVE CONTROL, per file: the scanner really did see this file's code.
            Assert.False(string.IsNullOrWhiteSpace(code), name + " produced empty code text");

            foreach (string token in PlannerTokens)
            {
                Assert.DoesNotContain(token, code, StringComparison.Ordinal);
            }
        }

        // POSITIVE CONTROL with teeth: the same reader DOES find real declarations.
        Assert.Contains(
            "class StartupProbeWorker",
            CSharpLexicalScanner.ExtractCode(File.ReadAllText(Path.Combine(
                ServiceCookPreparationBoundaryTests.ServiceSourceRoot(), "StartupProbeWorker.cs"))),
            StringComparison.Ordinal);
    }

    // ---- no new capability entered the service --------------------------------

    public static TheoryData<string> ForbiddenCapabilityTokens() => new()
    {
        // certificate and private key
        "X509", "CngKey", "CngProvider", "RSACng", "SafeNCryptKeyHandle", "CertificateRequest",
        // ACL and identity
        "AccessControl", "CryptoKeySecurity", "CryptoKeyRights", "FileSecurity",
        "FileSystemAccessRule", "SecurityIdentifier", "WindowsIdentity", "RawSecurityDescriptor",
        "SetAccessControl", "GetAccessControl",
        // registry and credential vault
        "RegistryKey", "Microsoft.Win32.Registry", "CredRead", "CredWrite", "PasswordVault",
        // service control and elevation
        "ServiceController", "ServiceInstaller", "sc.exe", "runas", "ShellExecute",
        // process start
        "Process.Start", "ProcessStartInfo", "Start-Process",
        // network
        "HttpClient", "WebClient", "HttpRequestMessage", "Socket", "TcpClient", "WebRequest",
        // desktop host and React
        "PAXCookbook.App", "web-react", "WebView2", "CoreWebView2",
        // PAX and Bake
        "PAX_Purview", "PaxEngine", "StartBake", "Start-Bake", "startCook",
    };

    [Theory]
    [MemberData(nameof(ForbiddenCapabilityTokens))]
    public void The_service_sources_gained_no_new_capability_under_the_lexical_scanner(string token)
    {
        string[] files = ServiceCookPreparationBoundaryTests.ServiceSourceFiles();
        Assert.NotEmpty(files);

        string[] offenders = files
            .Where(f => CSharpLexicalScanner.ExtractCode(File.ReadAllText(f))
                .Contains(token, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToArray();

        Assert.Empty(offenders);
    }

    [Theory]
    [MemberData(nameof(ForbiddenCapabilityTokens))]
    public void The_lexical_capability_scanner_fires_on_a_synthetic_positive_control(string token)
    {
        Assert.Contains(token, CSharpLexicalScanner.ExtractCode("var offending = " + token + "();"), StringComparison.Ordinal);
        Assert.DoesNotContain(token, CSharpLexicalScanner.ExtractCode("// " + token), StringComparison.Ordinal);
        Assert.DoesNotContain(token, CSharpLexicalScanner.ExtractCode("var s = \"" + token + "\";"), StringComparison.Ordinal);
    }

    // ---- ruling 7 controls: the cases the OLD heuristic stripper got wrong -----

    [Fact]
    public void The_lexical_scanner_beats_the_old_heuristic_stripper_in_both_directions()
    {
        // (1) THE DANGEROUS ONE. "//" inside a string used to truncate the line and
        //     HIDE the real call that followed. The old stripper reported nothing.
        const string hiddenCall = "var url = \"http://example\"; new HttpClient();";
        Assert.Contains("HttpClient", CSharpLexicalScanner.ExtractCode(hiddenCall), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HttpClient",
            ServiceCookPreparationBoundaryTests.StripComments(hiddenCall),
            StringComparison.Ordinal);

        // (2) FALSE POSITIVE. An ordinary string literal naming a capability used to
        //     be reported as capability CODE, because the old stripper only ever
        //     removed comments and had no idea what a string literal was.
        const string tokenInsideLiteral = "var s = \"X509Store\"; var q = 3;";
        Assert.DoesNotContain(
            "X509Store", CSharpLexicalScanner.ExtractCode(tokenInsideLiteral), StringComparison.Ordinal);
        Assert.Contains(
            "X509Store",
            ServiceCookPreparationBoundaryTests.StripComments(tokenInsideLiteral),
            StringComparison.Ordinal);
        Assert.Contains(
            "var q = 3;", CSharpLexicalScanner.ExtractCode(tokenInsideLiteral), StringComparison.Ordinal);

        // (3) The explicitly required control: a string literal containing
        //     "// X509Store" must NOT be reported as code.
        Assert.DoesNotContain(
            "X509Store",
            CSharpLexicalScanner.ExtractCode("var s = \"// X509Store\";"),
            StringComparison.Ordinal);

        // (4) RAW triple-quoted strings. The old stripper had no concept of them at
        //     all and reported their contents as code.
        string raw = "var s = \"\"\"\nX509Store\n\"\"\";\nvar after = 1;";
        Assert.DoesNotContain("X509Store", CSharpLexicalScanner.ExtractCode(raw), StringComparison.Ordinal);
        Assert.Contains("var after = 1;", CSharpLexicalScanner.ExtractCode(raw), StringComparison.Ordinal);
        Assert.Contains(
            "X509Store",
            ServiceCookPreparationBoundaryTests.StripComments(raw),
            StringComparison.Ordinal);

        // (5) INTERPOLATION HOLES ARE CODE and must survive, or the scanner would
        //     become a new hiding place.
        Assert.Contains(
            "HttpClient",
            CSharpLexicalScanner.ExtractCode("var s = $\"{new HttpClient()}\";"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "X509Store",
            CSharpLexicalScanner.ExtractCode("var s = $\"X509Store {value}\";"),
            StringComparison.Ordinal);

        // (6) A character literal holding a quote must not swallow the rest of the
        //     file as a string.
        Assert.Contains(
            "HttpClient",
            CSharpLexicalScanner.ExtractCode("char q = '\"'; new HttpClient();"),
            StringComparison.Ordinal);

        // (7) Escapes: an escaped quote does not close the literal.
        Assert.DoesNotContain(
            "X509Store",
            CSharpLexicalScanner.ExtractCode("var s = \"\\\" X509Store\"; var q = 1;"),
            StringComparison.Ordinal);
        Assert.Contains(
            "var q = 1;",
            CSharpLexicalScanner.ExtractCode("var s = \"\\\" X509Store\"; var q = 1;"),
            StringComparison.Ordinal);

        // (8) Doubled braces in an interpolated string are literal text, not a hole.
        Assert.DoesNotContain(
            "X509Store",
            CSharpLexicalScanner.ExtractCode("var s = $\"{{X509Store}}\";"),
            StringComparison.Ordinal);

        // (9) A verbatim string's doubled quote does not close it early.
        Assert.DoesNotContain(
            "X509Store",
            CSharpLexicalScanner.ExtractCode("var s = @\"a\"\"b X509Store\"; var q = 2;"),
            StringComparison.Ordinal);
        Assert.Contains(
            "var q = 2;",
            CSharpLexicalScanner.ExtractCode("var s = @\"a\"\"b X509Store\"; var q = 2;"),
            StringComparison.Ordinal);
    }

    // ---- the linked planner is inert in this host -----------------------------

    [Fact]
    public void The_linked_planner_never_authorises_a_grant_inside_the_service_host()
    {
        // 1. The vocabulary carries no executable verb, in THIS assembly.
        Assembly assembly = typeof(ServiceContract).Assembly;
        var enumMembers = new List<string>();
        foreach (Type type in assembly.GetTypes())
        {
            if (type.IsEnum)
            {
                enumMembers.AddRange(Enum.GetNames(type));
            }
        }

        Assert.NotEmpty(enumMembers);
        foreach (string forbidden in new[]
                 {
                     "ApplyGrant", "GrantAccess", "ImportCertificate", "StartService",
                     "RunCook", "DeleteReferencedCertificate",
                 })
        {
            Assert.DoesNotContain(forbidden, enumMembers);
        }

        // 2. Promotion is refused with zero actions. Cycle 42 approved one rights
        // profile and the planner refusal did NOT weaken as a result.
        Assert.True(ServiceOwnershipLedgerContract.HasApprovedRightsProfile);

        ServiceOwnershipLedgerValidationResult absent = ServiceOwnershipLedgerValidator.ForAbsentLedger();

        ServiceOwnershipLifecyclePlanRequest? assess =
            ServiceOwnershipLifecyclePlanRequest.ForAssessPromotion(
                absent, "install-0001",
                "S-1-5-21-1111111111-2222222222-3333333333-1001",
                "S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890");
        Assert.NotNull(assess);

        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(assess);
        Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);
        Assert.Equal(ServiceOwnershipPlanRefusalReason.PromotionNotAuthorized, plan.RefusalReason);
        Assert.Empty(plan.Actions);

        // 3. A null request refuses rather than throwing, inside this host too.
        ServiceOwnershipLifecyclePlan nullPlan = ServiceOwnershipLifecyclePlanner.Plan(null);
        Assert.Equal(ServiceOwnershipPlanOutcome.Refused, nullPlan.Outcome);
        Assert.Equal(ServiceOwnershipPlanRefusalReason.InvalidRequest, nullPlan.RefusalReason);
        Assert.Empty(nullPlan.Actions);
    }

    [Fact]
    public void The_planner_declares_no_io_shaped_dependency()
    {
        // A planner that referenced a stream, a path, a certificate, a registry key
        // or a socket ANYWHERE in its public or private surface would not be a pure
        // decision function. Its own source file is read here as text, through the
        // lexical scanner, so a mention in a comment cannot mask a real reference.
        string path = Path.Combine(
            ServiceCookPreparationBoundaryTests.RepoRoot(),
            "src", "PAXCookbook.Shared", "Contracts", "ServiceOwnershipLifecyclePlanner.cs");

        Assert.True(File.Exists(path));
        string code = CSharpLexicalScanner.ExtractCode(File.ReadAllText(path));
        Assert.False(string.IsNullOrWhiteSpace(code));

        foreach (string token in new[]
                 {
                     "System.IO", "File.", "Directory.", "Stream", "Path.Combine",
                     "Registry", "X509", "Cng", "HttpClient", "Socket", "Process",
                     "ServiceController", "Environment.", "AppContext", "DllImport",
                     "Marshal", "unsafe",
                 })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }

        // POSITIVE CONTROL: the file really was read and really is the planner.
        Assert.Contains("ServiceOwnershipLifecyclePlanner", code, StringComparison.Ordinal);
        Assert.Contains("RestoreCapturedPriorDacl", code, StringComparison.Ordinal);
    }
}
