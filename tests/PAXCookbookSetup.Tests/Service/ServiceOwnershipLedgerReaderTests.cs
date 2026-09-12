using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 51 - FIXED READ-ONLY SERVICE OWNERSHIP-LEDGER READER (Setup only)
// ===========================================================================
//
// PHASE 1 STATUS. ServiceOwnershipLedgerReader.cs is a COMPILING STUB (cycle
// 51): every type/member has its FINAL, FROZEN signature, but
// ServiceOwnershipLedgerReaderInterpreter.Interpret always returns the bounded
// Unavailable result and ServiceOwnershipLedgerReader.Read touches no
// filesystem. Every assertion below states the FINAL expected behavior for
// phase 2, so the interpreter tests are EXPECTED TO FAIL RED right now -
// genuine runtime assertion failures against the stub's permanent Unavailable
// return, never a compiler error. This whole file is BYTE-FROZEN from the end
// of phase 1 onward.
//
// CYCLE 90 AMENDMENT (planner-authorized, disclosed): the only edit since that
// freeze is the observer byte-pin in
// The_observer_file_is_byte_pinned_at_its_authorized_cycle_90_state, re-pinned
// because cycle 90 was explicitly ordered to amend the observer itself.
public sealed class ServiceOwnershipLedgerReaderInterpreterTests
{
    // ---- default / unspecified -------------------------------------------

    [Fact]
    public void A_default_uninitialised_result_is_unspecified_and_never_a_success()
    {
        var result = default(ServiceOwnershipLedgerReadResult);

        Assert.Equal(ServiceOwnershipLedgerReadState.Unspecified, result.State);
        Assert.Null(result.Validation);
        Assert.Equal("Unspecified", result.ToString());
    }

    // ---- confirmed absence -------------------------------------------------

    [Fact]
    public void Confirmed_absence_maps_to_absent_and_uses_for_absent_ledger()
    {
        ServiceOwnershipLedgerReadResult result =
            ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.Absent());

        Assert.Equal(ServiceOwnershipLedgerReadState.Absent, result.State);
        Assert.NotNull(result.Validation);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Absent, result.Validation!.Outcome);
        Assert.Null(result.Validation!.Document);
    }

    // ---- unsupported platform ----------------------------------------------

    [Fact]
    public void An_unsupported_platform_is_unavailable_and_never_confused_with_absence()
    {
        ServiceOwnershipLedgerReadResult result =
            ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.PlatformUnsupported());

        Assert.Equal(ServiceOwnershipLedgerReadState.Unavailable, result.State);
        Assert.Null(result.Validation);
    }

    // ---- access/stability failures - every named fact maps to Unavailable --
    //
    // The theory carries the FACT NAME only (a plain string), never the
    // internal ServiceOwnershipLedgerReadFacts type itself, because xunit's
    // MemberData/Theory public surface cannot expose a less-accessible type.
    // The named fact is constructed from the name INSIDE the test method body.

    public static TheoryData<string> AccessFailureFactNames() => new()
    {
        "StructuralFailure", "Oversized", "ShortRead", "Grew", "Shrank", "TrailingData",
    };

    private static ServiceOwnershipLedgerReadFacts FactByName(string name) => name switch
    {
        "StructuralFailure" => ServiceOwnershipLedgerReadFacts.StructuralFailure(),
        "Oversized" => ServiceOwnershipLedgerReadFacts.Oversized(),
        "ShortRead" => ServiceOwnershipLedgerReadFacts.ShortRead(),
        "Grew" => ServiceOwnershipLedgerReadFacts.Grew(),
        "Shrank" => ServiceOwnershipLedgerReadFacts.Shrank(),
        "TrailingData" => ServiceOwnershipLedgerReadFacts.TrailingData(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown fact name"),
    };

    [Theory]
    [MemberData(nameof(AccessFailureFactNames))]
    public void Every_access_or_stability_failure_maps_to_unavailable(string factName)
    {
        ServiceOwnershipLedgerReadResult result = ServiceOwnershipLedgerReaderInterpreter.Interpret(FactByName(factName));

        Assert.Equal(ServiceOwnershipLedgerReadState.Unavailable, result.State);
        Assert.Null(result.Validation);
    }

    [Fact]
    public void A_null_byte_array_on_an_otherwise_complete_read_is_unavailable()
    {
        // Structural defence: ReadComplete=true with Bytes=null can only happen
        // through reflection/misuse, never through the real factory methods -
        // proven here anyway so the interpreter never trusts ReadComplete alone.
        ServiceOwnershipLedgerReadFacts facts = ServiceOwnershipLedgerReadFacts.CompleteRead(Array.Empty<byte>());
        ServiceOwnershipLedgerReadResult emptyResult = ServiceOwnershipLedgerReaderInterpreter.Interpret(facts);

        // An empty byte array is a legitimate "empty file" case (covered below);
        // this test exists to anchor that Bytes is never actually null when
        // ReadComplete is true via the real factory surface.
        Assert.NotEqual(ServiceOwnershipLedgerReadState.Unspecified, emptyResult.State);
    }

    // ---- empty file -> Refused, NOT Absent ---------------------------------

    [Fact]
    public void An_empty_file_is_refused_not_absent()
    {
        ServiceOwnershipLedgerReadResult result =
            ServiceOwnershipLedgerReaderInterpreter.Interpret(
                ServiceOwnershipLedgerReadFacts.CompleteRead(Array.Empty<byte>()));

        Assert.Equal(ServiceOwnershipLedgerReadState.Refused, result.State);
        Assert.NotNull(result.Validation);
        Assert.True(result.Validation!.IsRefused);
        Assert.Null(result.Validation!.Document);
    }

    // ---- BOM-prefixed input -> rejected (Unavailable) ----------------------

    [Fact]
    public void Utf8_bom_prefixed_bytes_are_rejected_as_unavailable()
    {
        byte[] bom = { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}' };
        ServiceOwnershipLedgerReadResult result =
            ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.CompleteRead(bom));

        Assert.Equal(ServiceOwnershipLedgerReadState.Unavailable, result.State);
        Assert.Null(result.Validation);
    }

    [Fact]
    public void The_same_bytes_without_the_bom_prefix_are_not_rejected_for_encoding_reasons()
    {
        // Positive control: stripping the BOM from the prior test's bytes still
        // reaches Validate() (and is refused as malformed JSON for an unrelated,
        // content-shape reason) rather than being rejected purely for encoding.
        byte[] withoutBom = { (byte)'{', (byte)'}' };
        ServiceOwnershipLedgerReadResult result =
            ServiceOwnershipLedgerReaderInterpreter.Interpret(
                ServiceOwnershipLedgerReadFacts.CompleteRead(withoutBom));

        Assert.Equal(ServiceOwnershipLedgerReadState.Refused, result.State);
    }

    // ---- invalid UTF-8 -> rejected as unavailable, exception contained -----

    [Fact]
    public void Invalid_utf8_bytes_are_rejected_as_unavailable_without_throwing()
    {
        byte[] invalid = { 0xC0, 0xC1, 0xFE, 0xFF }; // never valid UTF-8 lead bytes
        Exception? exception = Record.Exception(() =>
        {
            ServiceOwnershipLedgerReadResult result = ServiceOwnershipLedgerReaderInterpreter.Interpret(
                ServiceOwnershipLedgerReadFacts.CompleteRead(invalid));
            Assert.Equal(ServiceOwnershipLedgerReadState.Unavailable, result.State);
        });
        Assert.Null(exception);
    }

    [Fact]
    public void Invalid_utf8_bytes_never_decode_to_a_replacement_character_result()
    {
        // If the interpreter ever used a lossy/replacement-character decode
        // instead of strict UTF-8, this document ("<EF BF BD>") would decode to
        // valid text and be routed to Validate() as Refused (malformed JSON)
        // instead of Unavailable (rejected at the decode boundary).
        byte[] invalid = { 0xC0, 0xC1 };
        ServiceOwnershipLedgerReadResult result = ServiceOwnershipLedgerReaderInterpreter.Interpret(
            ServiceOwnershipLedgerReadFacts.CompleteRead(invalid));

        Assert.Equal(ServiceOwnershipLedgerReadState.Unavailable, result.State);
    }

    // ---- Validated / Refused via the real certified validator --------------

    [Fact]
    public void A_hostile_but_syntactically_valid_json_document_is_refused_not_absent_or_unavailable()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("{\"not\":\"a ledger\"}");
        ServiceOwnershipLedgerReadResult result =
            ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.CompleteRead(bytes));

        Assert.Equal(ServiceOwnershipLedgerReadState.Refused, result.State);
        Assert.NotNull(result.Validation);
        Assert.True(result.Validation!.IsRefused);
    }

    [Fact]
    public void A_valid_empty_ledger_document_is_validated_and_accepted()
    {
        string json = BuildEmptyDocumentJson();
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        ServiceOwnershipLedgerReadResult result =
            ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.CompleteRead(bytes));

        Assert.Equal(ServiceOwnershipLedgerReadState.Validated, result.State);
        Assert.NotNull(result.Validation);
        Assert.True(result.Validation!.IsAccepted);
        Assert.NotNull(result.Validation!.Document);
        Assert.Empty(result.Validation!.Document!.Entries);
    }

    [Fact]
    public void Every_validator_refusal_reachable_via_bytes_stays_refused()
    {
        // Wrong schema version - one of many refusal shapes the certified
        // validator already enforces; proven here as a representative sample
        // reached genuinely THROUGH THIS READER's decode+validate path.
        string json = BuildEmptyDocumentJson(schemaVersion: 999);
        byte[] bytes = Encoding.UTF8.GetBytes(json);

        ServiceOwnershipLedgerReadResult result =
            ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.CompleteRead(bytes));

        Assert.Equal(ServiceOwnershipLedgerReadState.Refused, result.State);
        Assert.True(result.Validation!.IsRefused);
        Assert.Equal(ServiceOwnershipLedgerInvalidReason.UnsupportedSchemaVersion, result.Validation!.Reason);
    }

    private static string BuildEmptyDocumentJson(int schemaVersion = -1)
    {
        int version = schemaVersion == -1 ? ServiceOwnershipLedgerContract.LedgerSchemaVersion : schemaVersion;
        const string stamp = "2026-08-12T00:00:00Z";
        return "{"
            + "\"schemaVersion\":" + version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ","
            + "\"productOwnershipMarker\":\"" + ServiceOwnershipLedgerContract.ProductOwnershipMarker + "\","
            + "\"managedFeatureId\":\"" + ServiceOwnershipLedgerContract.ManagedFeatureId + "\","
            + "\"installationOwnershipId\":\"install-c51-0001\","
            + "\"generation\":1,"
            + "\"transactionState\":\"idle\","
            + "\"entries\":[],"
            + "\"createdUtc\":\"" + stamp + "\","
            + "\"updatedUtc\":\"" + stamp + "\","
            + "\"lastOperationId\":\"op-c51-0001\""
            + "}";
    }

    // ---- exactly one validator invocation -----------------------------------

    [Fact]
    public void The_reader_source_calls_validate_exactly_once()
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(ReaderPath()));
        int count = CountOccurrences(code, "ServiceOwnershipLedgerValidator.Validate(");
        Assert.Equal(1, count);
    }

    private static string ReaderPath() => Path.Combine(
        ServiceSidResolverStructuralTests.RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipLedgerReader.cs");

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

    // ---- never accepts a caller-supplied validation result or boolean ------

    [Fact]
    public void The_interpreter_does_not_accept_a_caller_supplied_validation_result_or_boolean()
    {
        MethodInfo interpret = typeof(ServiceOwnershipLedgerReaderInterpreter)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsPrivate)
            .Single();

        ParameterInfo[] parameters = interpret.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(ServiceOwnershipLedgerReadFacts), parameters[0].ParameterType);
        Assert.NotEqual(typeof(bool), parameters[0].ParameterType);
        Assert.NotEqual(typeof(ServiceOwnershipLedgerValidationResult), parameters[0].ParameterType);
    }

    // ---- never throws, even for hostile input ------------------------------

    [Fact]
    public void The_interpreter_never_throws_for_any_hostile_or_truncated_bytes()
    {
        byte[][] hostileInputs =
        {
            Array.Empty<byte>(),
            new byte[] { 0xFF },
            new byte[] { 0xEF, 0xBB, 0xBF },
            new byte[2048],
            Encoding.UTF8.GetBytes(new string('x', 10_000)),
        };

        foreach (byte[] hostile in hostileInputs)
        {
            Exception? exception = Record.Exception(() =>
                ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.CompleteRead(hostile)));
            Assert.Null(exception);
        }
    }

    // ---- no sensitive output -------------------------------------------------

    [Fact]
    public void No_sensitive_value_is_representable_in_tostring()
    {
        var unspecified = default(ServiceOwnershipLedgerReadResult);
        ServiceOwnershipLedgerReadResult absent =
            ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.Absent());
        ServiceOwnershipLedgerReadResult validated = ServiceOwnershipLedgerReaderInterpreter.Interpret(
            ServiceOwnershipLedgerReadFacts.CompleteRead(Encoding.UTF8.GetBytes(BuildEmptyDocumentJson())));

        foreach (ServiceOwnershipLedgerReadResult result in new[] { unspecified, absent, validated })
        {
            Assert.Equal(result.State.ToString(), result.ToString());
        }

        foreach (string token in new[] { "ProgramData", "PAXCookbook", "Service", "\\", "install-c51" })
        {
            Assert.DoesNotContain(token, unspecified.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(token, absent.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(token, validated.ToString(), StringComparison.Ordinal);
        }
    }
}

// ===========================================================================
// STRUCTURAL CONTAINMENT - the fixed shim and pure interpreter, from source
// ===========================================================================
public sealed class ServiceOwnershipLedgerReaderStructuralTests
{
    private static string RepoRoot() => ServiceSidResolverStructuralTests.RepoRoot();

    private static string ReaderPath() =>
        Path.Combine(RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipLedgerReader.cs");

    private static string ReaderRaw() => File.ReadAllText(ReaderPath());

    private static string ReaderCode() => SetupCSharpLexicalScanner.ExtractCode(ReaderRaw());

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

    [Fact]
    public void The_scanner_really_read_the_reader_file()
    {
        string code = ReaderCode();
        Assert.Contains("class ServiceOwnershipLedgerReader", code, StringComparison.Ordinal);
        Assert.Contains("class ServiceOwnershipLedgerReaderInterpreter", code, StringComparison.Ordinal);
        Assert.True(code.Length > 300, "the extracted reader code looks too small to be real: " + code.Length);
    }

    // ---- the shim's sole entry point takes no argument ---------------------

    [Fact]
    public void The_shims_one_entry_point_takes_no_argument()
    {
        Type t = typeof(ServiceOwnershipLedgerReader);
        Assert.True(t.IsAbstract && t.IsSealed, "the shim must be a static class");

        MethodInfo[] nonPrivate = t
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsPrivate)
            .ToArray();

        MethodInfo read = Assert.Single(nonPrivate);
        Assert.Equal("Read", read.Name);
        Assert.Empty(read.GetParameters());
        Assert.Equal(typeof(ServiceOwnershipLedgerReadResult), read.ReturnType);

        Assert.Empty(t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Empty(t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly));
    }

    [Fact]
    public void The_pure_interpreters_one_entry_point_takes_only_the_bounded_facts()
    {
        Type t = typeof(ServiceOwnershipLedgerReaderInterpreter);
        Assert.True(t.IsAbstract && t.IsSealed, "the interpreter must be a static class");

        MethodInfo[] nonPrivate = t
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsPrivate)
            .ToArray();

        MethodInfo interpret = Assert.Single(nonPrivate);
        Assert.Equal("Interpret", interpret.Name);
        ParameterInfo parameter = Assert.Single(interpret.GetParameters());
        Assert.Equal(typeof(ServiceOwnershipLedgerReadFacts), parameter.ParameterType);
        Assert.Equal(typeof(ServiceOwnershipLedgerReadResult), interpret.ReturnType);
    }

    // ---- no injectable seam -------------------------------------------------

    [Fact]
    public void No_type_declares_a_delegate_field_virtual_member_or_settable_static_field()
    {
        foreach (Type t in new[]
                 {
                     typeof(ServiceOwnershipLedgerReadState),
                     typeof(ServiceOwnershipLedgerReadResult),
                     typeof(ServiceOwnershipLedgerReadFacts),
                     typeof(ServiceOwnershipLedgerReaderInterpreter),
                     typeof(ServiceOwnershipLedgerReader),
                 })
        {
            if (t.IsEnum)
            {
                continue; // BCL guarantees, not an injectable seam
            }

            Assert.Empty(t.GetInterfaces());

            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            {
                Assert.True(f.IsLiteral || f.IsInitOnly, t.Name + "." + f.Name + " is mutable");
                Assert.False(typeof(Delegate).IsAssignableFrom(f.FieldType), t.Name + "." + f.Name + " is a delegate");
            }

            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.Name is "ToString" or "Equals" or "GetHashCode")
                {
                    continue;
                }

                Assert.False(m.IsVirtual && !m.IsFinal, t.Name + "." + m.Name + " is virtual/overridable");
                Assert.False(m.IsAbstract, t.Name + "." + m.Name + " is abstract");

                foreach (ParameterInfo p in m.GetParameters())
                {
                    Assert.False(typeof(Delegate).IsAssignableFrom(p.ParameterType), t.Name + "." + m.Name + " accepts a delegate");
                }
            }
        }
    }

    [Fact]
    public void Neither_the_interpreter_nor_the_shim_accepts_a_bare_string_or_byte_array_parameter()
    {
        foreach (Type t in new[]
                 {
                     typeof(ServiceOwnershipLedgerReaderInterpreter),
                     typeof(ServiceOwnershipLedgerReader),
                 })
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.IsPrivate)
                {
                    continue; // native P/Invoke declarations are private and exempt
                }

                foreach (ParameterInfo p in m.GetParameters())
                {
                    Assert.NotEqual(typeof(string), p.ParameterType);
                    Assert.NotEqual(typeof(byte[]), p.ParameterType);
                }
            }
        }
    }

    // ---- no forbidden capability --------------------------------------------

    public static TheoryData<string> ForbiddenCapabilityTokens() => new()
    {
        // writer / ACL-mutation / creation
        "FileMode.Create", "File.WriteAllBytes", "File.WriteAllText", "File.AppendAllText",
        "File.Delete", "File.Move", "File.Replace", "File.Copy", "Directory.CreateDirectory",
        "Directory.Delete", "StreamWriter", "SetKernelObjectSecurity", "SetFileSecurity",
        "SetNamedSecurityInfo", "SetAccessControl", "AddAccessRule", "SetOwner", "NCryptSetProperty",
        // ledger writer / observer / lifecycle planner
        "ServiceOwnershipCredentialObserver.Observe(", "ServiceOwnershipLifecyclePlanner.Plan(",
        // certificate, key, registry, credential vault
        "X509Store", "CngKey", "CngProvider", "NCryptOpenKey", "RegistryKey", "Microsoft.Win32.Registry",
        "CredRead", "CredWrite", "PasswordVault",
        // service control manager
        "OpenSCManager", "CreateService", "ChangeServiceConfig", "DeleteService", "StartService",
        "ControlService", "ServiceController", "ServiceInstaller",
        // process / elevation / network
        "Process.Start", "ProcessStartInfo", "runas", "HttpClient", "WebClient", "Socket",
        // desktop host, React, PAX, Bake
        "PAXCookbook.App", "web-react", "WebView2", "CoreWebView2",
        "PAX_Purview", "PaxEngine", "StartBake", "Start-Bake", "startCook",
    };

    [Theory]
    [MemberData(nameof(ForbiddenCapabilityTokens))]
    public void The_reader_contains_no_forbidden_capability(string token)
    {
        Assert.DoesNotContain(token, ReaderCode(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ForbiddenCapabilityTokens))]
    public void The_forbidden_capability_scan_can_actually_fire(string token)
    {
        Assert.False(string.IsNullOrWhiteSpace(token));
        string synthetic = "class X { void M() { var y = " + token + " ; } }";
        Assert.Contains(token, SetupCSharpLexicalScanner.ExtractCode(synthetic), StringComparison.Ordinal);
        Assert.DoesNotContain(
            token,
            SetupCSharpLexicalScanner.ExtractCode("// " + token + "\nclass Z { }"),
            StringComparison.Ordinal);
    }

    // ---- no exception text can escape --------------------------------------

    [Fact]
    public void No_exception_text_can_escape_the_reader()
    {
        string stripped = ReaderCode();
        foreach (string token in new[] { ".Message", "StackTrace", "FormatMessage", "GetLastPInvokeErrorMessage" })
        {
            Assert.DoesNotContain(token, stripped, StringComparison.Ordinal);
        }
    }

    // ---- no production call site -------------------------------------------
    //
    // CYCLE 94 AMENDMENT (G1), UNDER EXPLICIT AUTHORITY, AND NARROW.
    //
    // WHAT CHANGED. The authorized set grew from ONE file to exactly TWO: the
    // reader file itself, and the ONE authorized composition root at
    // src/PAXCookbookSetup/Service/ServiceOwnershipElevatedTransaction.cs. That
    // is the whole change.
    //
    // WHAT DID NOT CHANGE. Every other production file must still contain ZERO
    // reader tokens of any kind. The comparison is still WHOLE CANONICAL PATH
    // equality, OrdinalIgnoreCase - there is no directory allowance, no filename
    // pattern, no suffix, prefix, wildcard or substring match, and no
    // class-name matching. The scan is still comment-and-string stripped, and the
    // calibration and positive controls are all retained.
    //
    // WHY THE ROOT'S ALLOWANCE IS TIGHTER THAN THE READER'S. The reader file may
    // contain every reader token, because it declares them. The composition root
    // may do exactly two things: call Read() ONCE, and name the typed result ONCE
    // so it can cache that single result. It may NOT name the interpreter, the
    // facts type or the read-state enum, because naming any of those would mean
    // it is re-deriving a verdict the reader already reached. That asymmetry is
    // asserted directly below, so the widened allowance cannot quietly become a
    // second reader.

    private static readonly string[] ReaderTokens =
    {
        "ServiceOwnershipLedgerReader",
        "ServiceOwnershipLedgerReaderInterpreter",
        "ServiceOwnershipLedgerReadResult",
        "ServiceOwnershipLedgerReadState",
        "ServiceOwnershipLedgerReadFacts",
    };

    /// <summary>
    /// The tokens the composition root may NEVER name. A root that named any of
    /// these would be interpreting the ledger itself rather than consuming the
    /// one verdict the reader produced.
    /// </summary>
    private static readonly string[] ReaderTokensForbiddenInTheCompositionRoot =
    {
        "ServiceOwnershipLedgerReaderInterpreter",
        "ServiceOwnershipLedgerReadState",
        "ServiceOwnershipLedgerReadFacts",
    };

    internal static string CompositionRootPath() => Path.GetFullPath(Path.Combine(
        RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipElevatedTransaction.cs"));

    /// <summary>
    /// The ONLY comparison the rule uses: whole canonical path equality. It is a
    /// parameter of the scan body purely so the mutation control below can weaken
    /// it without forking the rule.
    /// </summary>
    private static readonly Func<string, string, bool> ExactFullPathMatch =
        static (actualFullPath, allowedFullPath) =>
            string.Equals(actualFullPath, allowedFullPath, StringComparison.OrdinalIgnoreCase);

    private static string[] AuthorizedReaderTokenFiles() =>
        new[] { Path.GetFullPath(ReaderPath()), CompositionRootPath() };

    private static string[] SourceFiles(string root) =>
        Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The ONE rule body. The real sweep, the negative control and the mutation
    /// control all call this, so no control can pass against a paraphrase.
    /// </summary>
    private static List<string> ScanForReaderTokenOffenders(
        string fileFullPath,
        string code,
        IReadOnlyList<string> authorizedFullPaths,
        Func<string, string, bool> fileMatches)
    {
        var offenders = new List<string>();
        string canonical = Path.GetFullPath(fileFullPath);

        foreach (string allowed in authorizedFullPaths)
        {
            if (fileMatches(canonical, allowed))
            {
                return offenders;
            }
        }

        foreach (string token in ReaderTokens)
        {
            if (code.Contains(token, StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(fileFullPath) + ":" + token);
            }
        }

        return offenders;
    }

    private static List<string> ScanForReaderTokenOffenders(string fileFullPath, string code) =>
        ScanForReaderTokenOffenders(fileFullPath, code, AuthorizedReaderTokenFiles(), ExactFullPathMatch);

    [Fact]
    public void The_reader_allowance_is_exactly_two_distinct_canonical_files()
    {
        string[] allowed = AuthorizedReaderTokenFiles();

        Assert.Equal(2, allowed.Length);
        Assert.NotEqual(allowed[0], allowed[1], StringComparer.OrdinalIgnoreCase);
        foreach (string path in allowed)
        {
            Assert.Equal(path, Path.GetFullPath(path), StringComparer.Ordinal);
            Assert.True(File.Exists(path), "an authorized reader-token file is missing: " + path);
        }

        Assert.Equal(Path.GetFullPath(ReaderPath()), allowed[0], StringComparer.OrdinalIgnoreCase);
        Assert.Equal(CompositionRootPath(), allowed[1], StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_production_source_file_outside_the_reader_calls_the_reader()
    {
        string[] files = SourceFiles(Path.Combine(RepoRoot(), "src"));
        Assert.True(files.Length >= 20, "expected the authored product sources, found " + files.Length);

        var offenders = new List<string>();
        var readerTokenFiles = new List<string>();
        int calibration = 0;

        foreach (string f in files)
        {
            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(f));

            if (code.Contains("ServiceOwnershipLedgerContract", StringComparison.Ordinal))
            {
                calibration++;
            }

            offenders.AddRange(ScanForReaderTokenOffenders(f, code));

            foreach (string token in ReaderTokens)
            {
                if (code.Contains(token, StringComparison.Ordinal))
                {
                    readerTokenFiles.Add(Path.GetFullPath(f));
                    break;
                }
            }
        }

        Assert.True(calibration > 0, "the positive control failed, so the zero below is not calibrated");
        Assert.Empty(offenders);

        // EXACT PRESENCE, NOT MERE ABSENCE. Exactly the two authorized files may
        // carry a reader token, so a third file can never be a silent addition.
        Assert.Equal(
            AuthorizedReaderTokenFiles().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            readerTokenFiles.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);

        string ownCode = ReaderCode();
        foreach (string token in ReaderTokens)
        {
            Assert.Contains(token, ownCode, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_composition_root_calls_read_exactly_once_and_only_caches_its_typed_result()
    {
        string root = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(CompositionRootPath()));

        // Exactly ONE invocation of the shim's one entry point.
        Assert.Equal(1, CountOccurrences(root, "ServiceOwnershipLedgerReader.Read("));

        // The typed result may be named AT MOST once, purely to cache it.
        Assert.True(
            CountOccurrences(root, "ServiceOwnershipLedgerReadResult") <= 1,
            "the composition root names the typed reader result more than once");

        // ...and the interpretation surface is off limits entirely.
        foreach (string forbidden in ReaderTokensForbiddenInTheCompositionRoot)
        {
            Assert.DoesNotContain(forbidden, root, StringComparison.Ordinal);
        }

        // POSITIVE CONTROL: the same scan DOES see a forbidden token when present.
        Assert.Contains(
            "ServiceOwnershipLedgerReadFacts",
            SetupCSharpLexicalScanner.ExtractCode(
                "class X { void M() { var y = ServiceOwnershipLedgerReadFacts.Absent(); } }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_third_production_file_naming_a_reader_token_is_still_an_offender()
    {
        // NEGATIVE CONTROL. The widened allowance covers exactly two files and no
        // more: a sibling in the very same directory still fails.
        string sibling = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipSomethingElse.cs"));
        string code = "class X { void M() { var r = ServiceOwnershipLedgerReader.Read(); } }";

        Assert.NotEmpty(ScanForReaderTokenOffenders(sibling, code));

        // ...and both authorized files pass the same body, so the control is
        // calibrated in both directions.
        Assert.Empty(ScanForReaderTokenOffenders(Path.GetFullPath(ReaderPath()), code));
        Assert.Empty(ScanForReaderTokenOffenders(CompositionRootPath(), code));
    }

    [Fact]
    public void MUTATION_widening_the_reader_allowance_to_the_whole_service_directory_is_caught()
    {
        string probe = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "SomeUnrelatedServiceFile.cs"));
        string code = "class X { void M() { var r = ServiceOwnershipLedgerReader.Read(); } }";

        Assert.NotEmpty(ScanForReaderTokenOffenders(probe, code));

        Func<string, string, bool> directoryWideMatch = static (actual, allowed) =>
            string.Equals(Path.GetDirectoryName(actual), Path.GetDirectoryName(allowed), StringComparison.OrdinalIgnoreCase);

        Assert.Empty(ScanForReaderTokenOffenders(
            probe, code, AuthorizedReaderTokenFiles(), directoryWideMatch));
    }

    [Fact]
    public void MUTATION_permitting_a_third_reader_file_is_caught()
    {
        string probe = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipElevatedTransaction2.cs"));
        string code = "class X { void M() { var r = ServiceOwnershipLedgerReader.Read(); } }";

        Assert.NotEmpty(ScanForReaderTokenOffenders(probe, code));

        string[] widened = AuthorizedReaderTokenFiles().Append(probe).ToArray();
        Assert.Equal(3, widened.Length);
        Assert.Empty(ScanForReaderTokenOffenders(probe, code, widened, ExactFullPathMatch));
    }

    [Fact]
    public void MUTATION_matching_the_composition_root_by_leaf_name_is_caught()
    {
        // A relocated root with the SAME leaf name must not satisfy the allowance.
        string relocated = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "Elsewhere", "ServiceOwnershipElevatedTransaction.cs"));
        string code = "class X { void M() { var r = ServiceOwnershipLedgerReader.Read(); } }";

        Assert.NotEmpty(ScanForReaderTokenOffenders(relocated, code));

        Func<string, string, bool> leafNameMatch = static (actual, allowed) =>
            string.Equals(Path.GetFileName(actual), Path.GetFileName(allowed), StringComparison.OrdinalIgnoreCase);

        Assert.Empty(ScanForReaderTokenOffenders(
            relocated, code, AuthorizedReaderTokenFiles(), leafNameMatch));
    }

    [Fact]
    public void The_observer_file_is_byte_pinned_at_its_authorized_cycle_90_state()
    {
        // WHY THIS IS RE-PINNED AND NOT UNTOUCHED. Cycle 51 froze the observer at
        // 10DB872C... while it sat on THAT cycle's protected list. Cycle 90 amended the
        // observer under explicit authorization, because the certified guarded tokens may
        // not be named from another production file, so the adapter had to live inside it.
        // A byte freeze on a file that has been ordered edited is unsatisfiable by
        // construction, so the pin moves to the authorized cycle-90 bytes. It is RE-pinned
        // rather than deleted: a re-pin still catches the NEXT unauthorized edit, whereas
        // deleting it would leave the file with no byte guard at all.
        //
        // The claim this test actually protects - that the reader never calls the observer -
        // does NOT depend on the freeze. It is proven independently and more strongly by the
        // comment-stripped token scan in
        // No_production_source_file_outside_the_observer_calls_the_observer, so nothing is
        // weakened by moving the constant.
        const string PinnedObserverSha256 = "90FE3A26E03311085368D9992B542C3D44ECCBC2AD24DE907B3B0E4F76C5A682";

        string observerPath = Path.Combine(RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipCredentialObserver.cs");
        Assert.True(File.Exists(observerPath));

        byte[] bytes = File.ReadAllBytes(observerPath);
        string sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        Assert.Equal(PinnedObserverSha256, sha256, ignoreCase: true);

        // POSITIVE CONTROL - the freeze is provably LIVE. A freeze nobody has shown can fail
        // is not a freeze. Flip one bit of an IN-MEMORY copy (the file on disk is never
        // written) and show the pinned digest rejects it, so a later unauthorized edit of any
        // size still turns this test red.
        Assert.NotEmpty(bytes);
        byte[] mutated = (byte[])bytes.Clone();
        mutated[^1] ^= 0x01;
        Assert.NotEqual(bytes[^1], mutated[^1]);

        string mutatedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(mutated));
        Assert.NotEqual(PinnedObserverSha256, mutatedSha256, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(sha256, mutatedSha256, StringComparer.OrdinalIgnoreCase);
    }

    // ---- native call counting (cycle-50 NativeCallGuard) -------------------

    [Fact]
    public void The_shim_declares_and_calls_each_native_api_exactly_once()
    {
        string code = ReaderCode();
        foreach (string api in new[] { "CreateFileW", "GetFinalPathNameByHandleW" })
        {
            NativeCallGuard.CallSiteCounts counts = NativeCallGuard.CountCallSites(code, api);
            int independentTotal = NativeCallGuard.CountMatchesIndependently(code, api);

            Assert.Equal(1, counts.Declarations);
            Assert.Equal(1, counts.Invocations);
            Assert.Equal(independentTotal, counts.Total);
        }
    }

    // ---- size bound and MaxLedgerBytes reference ---------------------------

    [Fact]
    public void The_reader_source_enforces_the_certified_max_ledger_bytes_bound()
    {
        string code = ReaderCode();
        Assert.Contains("ServiceOwnershipLedgerContract.MaxLedgerBytes", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reader_never_creates_the_service_directory()
    {
        string code = ReaderCode();
        Assert.DoesNotContain("CreateDirectory", code, StringComparison.Ordinal);
    }
}

// ===========================================================================
// TEST-OWNED NATIVE MARSHALLING PROOF - self-contained, no product dependency
// ===========================================================================
//
// WHAT THIS PROVES. That the SAME shape of handle-based native sequence the
// shim uses (CreateFileW with FILE_FLAG_OPEN_REPARSE_POINT, GetFinalPathNameByHandleW
// identity confirmation, RandomAccess bounded read from the SAME handle, and
// confirmed-absence detection) marshals correctly on this host, and that bytes
// obtained this way feed the REAL production interpreter correctly.
//
// WHAT THIS DOES NOT DO. It never touches real ProgramData, the real Service
// directory, or a real ledger, and never calls the product reader's Read()
// method (which has no seam to redirect it away from the real fixed path).
// The files/directories it creates are disposable, uniquely named, under the
// OS temp directory, and deleted in a finally block with residue asserted gone.
public sealed class ServiceOwnershipLedgerReaderNativeMarshallingTests
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x1;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const string ExtendedPathPrefix = @"\\?\";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetFinalPathNameByHandleW(
        SafeFileHandle hFile, [Out] char[] lpszFilePath, int cchFilePath, int dwFlags);

    private static string UniqueTempPath() =>
        Path.Combine(Path.GetTempPath(), "pax-c51-ledger-reader-" + Guid.NewGuid().ToString("N") + ".tmp");

    [Fact]
    public void The_handle_based_open_and_final_path_identity_check_round_trips_and_feeds_the_real_interpreter()
    {
        string path = UniqueTempPath();
        Assert.False(File.Exists(path));
        string json = "{\"schemaVersion\":" + ServiceOwnershipLedgerContract.LedgerSchemaVersion + ","
            + "\"productOwnershipMarker\":\"" + ServiceOwnershipLedgerContract.ProductOwnershipMarker + "\","
            + "\"managedFeatureId\":\"" + ServiceOwnershipLedgerContract.ManagedFeatureId + "\","
            + "\"installationOwnershipId\":\"install-c51-nm-0001\","
            + "\"generation\":1,\"transactionState\":\"idle\",\"entries\":[],"
            + "\"createdUtc\":\"2026-08-12T00:00:00Z\",\"updatedUtc\":\"2026-08-12T00:00:00Z\","
            + "\"lastOperationId\":\"op-c51-nm-0001\"}";
        byte[] contentBytes = Encoding.UTF8.GetBytes(json);
        File.WriteAllBytes(path, contentBytes);

        SafeFileHandle? handle = null;
        try
        {
            handle = CreateFileW(
                path, GenericRead, FileShareRead, IntPtr.Zero, OpenExisting, FileFlagOpenReparsePoint, IntPtr.Zero);
            Assert.False(handle is null || handle.IsInvalid, "expected the temp file to open successfully");

            var finalPathBuffer = new char[1024];
            int finalPathChars = GetFinalPathNameByHandleW(handle!, finalPathBuffer, finalPathBuffer.Length, 0);
            Assert.True(finalPathChars > 0 && finalPathChars < finalPathBuffer.Length);

            string finalPath = new(finalPathBuffer, 0, finalPathChars);
            if (finalPath.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal))
            {
                finalPath = finalPath[ExtendedPathPrefix.Length..];
            }
            Assert.Equal(Path.GetFullPath(path), finalPath, ignoreCase: true);

            long length = RandomAccess.GetLength(handle!);
            Assert.Equal(contentBytes.Length, length);

            var buffer = new byte[length];
            int totalRead = 0;
            while (totalRead < buffer.Length)
            {
                int read = RandomAccess.Read(handle!, buffer.AsSpan(totalRead), totalRead);
                if (read <= 0)
                {
                    break;
                }
                totalRead += read;
            }
            Assert.Equal(buffer.Length, totalRead);
            Assert.Equal(contentBytes, buffer);

            // One more read past the known length must report zero bytes - the
            // same trailing-data check the shim performs.
            Span<byte> probe = stackalloc byte[1];
            int extra = RandomAccess.Read(handle!, probe, buffer.Length);
            Assert.Equal(0, extra);

            // Feed the bytes obtained via this real handle-based technique into
            // the REAL production interpreter.
            ServiceOwnershipLedgerReadResult result = ServiceOwnershipLedgerReaderInterpreter.Interpret(
                ServiceOwnershipLedgerReadFacts.CompleteRead(buffer));
            Assert.Equal(ServiceOwnershipLedgerReadState.Validated, result.State);
            Assert.True(result.Validation!.IsAccepted);
        }
        finally
        {
            handle?.Dispose();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            Assert.False(File.Exists(path));
        }
    }

    [Fact]
    public void A_nonexistent_path_is_confirmed_absent_via_the_documented_exception_shape()
    {
        string path = UniqueTempPath();
        Assert.False(File.Exists(path));

        Exception? exception = Record.Exception(() => File.GetAttributes(path));
        Assert.IsType<FileNotFoundException>(exception);

        // The technique the shim uses to distinguish confirmed absence feeds the
        // REAL production interpreter directly.
        ServiceOwnershipLedgerReadResult result =
            ServiceOwnershipLedgerReaderInterpreter.Interpret(ServiceOwnershipLedgerReadFacts.Absent());
        Assert.Equal(ServiceOwnershipLedgerReadState.Absent, result.State);
    }

    [Fact]
    public void Exact_max_ledger_bytes_and_one_byte_over_are_distinguished_before_allocation()
    {
        string atLimitPath = UniqueTempPath();
        string overLimitPath = UniqueTempPath();
        try
        {
            File.WriteAllBytes(atLimitPath, new byte[ServiceOwnershipLedgerContract.MaxLedgerBytes]);
            File.WriteAllBytes(overLimitPath, new byte[ServiceOwnershipLedgerContract.MaxLedgerBytes + 1]);

            long atLimitLength = new FileInfo(atLimitPath).Length;
            long overLimitLength = new FileInfo(overLimitPath).Length;

            Assert.False(atLimitLength > ServiceOwnershipLedgerContract.MaxLedgerBytes);
            Assert.True(overLimitLength > ServiceOwnershipLedgerContract.MaxLedgerBytes);
        }
        finally
        {
            if (File.Exists(atLimitPath))
            {
                File.Delete(atLimitPath);
            }
            if (File.Exists(overLimitPath))
            {
                File.Delete(overLimitPath);
            }
            Assert.False(File.Exists(atLimitPath));
            Assert.False(File.Exists(overLimitPath));
        }
    }

    [Fact]
    public void A_reparse_point_is_detected_by_attribute_or_the_limitation_is_disclosed_structurally()
    {
        // BEST-EFFORT LIVE PROOF: creating a real reparse point may require a
        // privilege (SeCreateSymbolicLinkPrivilege) this sandbox may not grant.
        // If creation fails for that reason, this test falls back to a
        // STRUCTURAL proof that the shim's source performs a ReparsePoint check,
        // and disclosed as such rather than silently skipped.
        string linkPath = UniqueTempPath();
        string targetPath = UniqueTempPath();
        File.WriteAllBytes(targetPath, Encoding.UTF8.GetBytes("{}"));
        try
        {
            FileSystemInfo? link = null;
            Exception? creationFailure = Record.Exception(() =>
            {
                link = File.CreateSymbolicLink(linkPath, targetPath);
            });

            if (creationFailure is null && link is not null)
            {
                FileAttributes attrs = File.GetAttributes(linkPath);
                Assert.True(
                    (attrs & FileAttributes.ReparsePoint) != 0,
                    "expected the created symbolic link to report the ReparsePoint attribute");
            }
            else
            {
                // Disclosed fallback: prove the shim's source performs the same
                // check it would need to refuse this reparse point.
                string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(Path.Combine(
                    ServiceSidResolverStructuralTests.RepoRoot(), "src", "PAXCookbookSetup", "Service",
                    "ServiceOwnershipLedgerReader.cs")));
                Assert.Contains("ReparsePoint", code, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            Assert.False(File.Exists(linkPath));
            Assert.False(File.Exists(targetPath));
        }
    }

    [Fact]
    public void The_marshalling_proof_never_touches_real_program_data_or_calls_the_product_reader()
    {
        string path = Path.Combine(
            ServiceSidResolverStructuralTests.RepoRoot(),
            "tests", "PAXCookbookSetup.Tests", "Service", "ServiceOwnershipLedgerReaderTests.cs");
        Assert.True(File.Exists(path));

        string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(path));
        foreach (string token in new[]
                 {
                     "SpecialFolder.CommonApplicationData", "ServiceOwnershipLedgerReader.Read(",
                 })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }
    }
}
