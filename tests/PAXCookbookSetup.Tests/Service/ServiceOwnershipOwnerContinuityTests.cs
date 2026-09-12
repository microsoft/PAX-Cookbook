using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 96 - OWNER CONTINUITY BETWEEN THE VALIDATED ANCHOR AND THE OWNER PORT
// ===========================================================================
//
// THE DEFECT THIS FILE WAS WRITTEN AGAINST. Before cycle 96 the composition
// root constructed a PARAMETERLESS owner-identity port and then discarded the
// kernel-authenticated identity it already held. The port re-read the anchor
// UNBOUND, so whatever SID and installation id the anchor happened to hold at
// read time became the promotion's owner. Nothing in the product required the
// SID it returned to be the one the channel had already authenticated.
//
// WHY EVERY PROBE HERE IS REFLECTION OR A SOURCE SCAN, AND NOT A DIRECT CALL.
// A sibling certified guard proves that NO test in this project constructs a
// fixed native shim, and this file must not become the first exception. The
// owner port's one read goes through the certified installation anchor store,
// which has no seam. So the SHAPE of the port is probed by reflection, the
// WIRING is probed by scanning the one composition root's own source, and every
// DECISION is exercised through the pure interpreter that owns it.
//
// THESE PROBES WERE CAPTURED FAILING, AT RUNTIME, BEFORE ANY PRODUCTION EDIT.
// They are reflection-shaped precisely so that they COMPILE against the
// pre-cycle-96 product and fail as ASSERTIONS rather than as build errors, which
// is what makes the RED evidence real rather than a compiler transcript.
//
// NOTHING HERE opens a certificate store, reopens a private key, reads or writes
// an ACL, reads or writes %ProgramData%, reads the registry, elevates, starts a
// process, opens a socket, runs PAX or starts a Bake.
public sealed class ServiceOwnershipOwnerContinuityTests
{
    private const string HonestSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";
    private const string SubstitutedSid = "S-1-5-21-1111111111-2222222222-3333333333-1002";
    private const string HonestInstallation = "8f2b1a4c-6d3e-4f5a-9b7c-0e1d2a3b4c5d";
    private const string SubstitutedInstallation = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";

    // ---- shared plumbing ---------------------------------------------------

    private static string RepoRoot() => ServiceOwnershipElevatedTransactionStructuralTests.RepoRoot();

    private static string CompositionRootPath() => Path.Combine(
        RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipElevatedTransaction.cs");

    private static string OwnerAdapterPath() => Path.Combine(
        RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipFixedOwnerIdentityAdapter.cs");

    private static string CompositionRootCode() =>
        SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(CompositionRootPath()));

    private static string[] ProductionSources() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    private const BindingFlags AllInstance =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>
    /// The ONE construction path this file uses for the bounded anchor facts. It
    /// asserts the required SHAPE first, so a product that does not carry the
    /// constructor-fixed expectations fails as a readable assertion instead of a
    /// raw reflection exception.
    /// </summary>
    private static ServiceOwnershipObservedAnchorFacts Facts(
        bool anchorValidated = true,
        bool anchorAccepted = true,
        bool documentPresent = true,
        string? observedInstallationId = HonestInstallation,
        string? observedInitiatingUserSid = HonestSid,
        string? expectedInstallationId = HonestInstallation,
        string? expectedInitiatingUserSid = HonestSid)
    {
        ConstructorInfo bound = Assert.Single(
            typeof(ServiceOwnershipObservedAnchorFacts).GetConstructors(AllInstance));

        ParameterInfo[] parameters = bound.GetParameters();

        // THE HEART OF THE RED. Four parameters means the facts carry only what
        // was OBSERVED, with nothing to compare it against; seven means the facts
        // also carry the two values the constructor fixed.
        Assert.Equal(7, parameters.Length);

        return (ServiceOwnershipObservedAnchorFacts)bound.Invoke(
            new object?[]
            {
                anchorValidated,
                anchorAccepted,
                documentPresent,
                observedInstallationId,
                observedInitiatingUserSid,
                expectedInstallationId,
                expectedInitiatingUserSid,
            });
    }

    private static ServiceOwnershipOwnerIdentityObservation Interpret(
        ServiceOwnershipObservedAnchorFacts facts) =>
        ServiceOwnershipOwnerIdentityInterpreter.Interpret(facts);

    // =======================================================================
    // 1. THE FACTS CARRY BOTH CONSTRUCTOR-FIXED VALUES, BY NAME AND BY TYPE
    // =======================================================================

    [Fact]
    public void The_bounded_anchor_facts_carry_the_two_constructor_fixed_expectations()
    {
        ConstructorInfo bound = Assert.Single(
            typeof(ServiceOwnershipObservedAnchorFacts).GetConstructors(AllInstance));

        string[] names = bound.GetParameters().Select(p => p.Name!).ToArray();

        Assert.Equal(
            new[]
            {
                "anchorValidated",
                "anchorAccepted",
                "documentPresent",
                "observedInstallationId",
                "observedInitiatingUserSid",
                "expectedInstallationId",
                "expectedInitiatingUserSid",
            },
            names);

        foreach (ParameterInfo parameter in bound.GetParameters().Skip(3))
        {
            Assert.Equal(typeof(string), parameter.ParameterType);
        }
    }

    // =======================================================================
    // 2. THE HONEST PATH SUCCEEDS, AND RETURNS THE CONSTRUCTOR-FIXED SID
    // =======================================================================

    [Fact]
    public void The_same_owner_and_the_same_installation_observe_the_constructor_fixed_owner()
    {
        ServiceOwnershipOwnerIdentityObservation observed = Interpret(Facts());

        Assert.Equal(ServiceOwnershipOwnerIdentityState.Observed, observed.State);
        Assert.True(observed.IsObserved);
        Assert.Equal(HonestSid, observed.OwningUserSid);
    }

    // =======================================================================
    // 3. SUBSTITUTION IS REFUSED IN BOTH DIRECTIONS
    // =======================================================================

    [Fact]
    public void A_substituted_owner_sid_on_the_same_installation_is_refused()
    {
        // The reread anchor names a DIFFERENT user while carrying the very
        // installation id the transaction already validated. That is exactly the
        // shape an ownership takeover would have.
        ServiceOwnershipOwnerIdentityObservation observed =
            Interpret(Facts(observedInitiatingUserSid: SubstitutedSid));

        Assert.False(observed.IsObserved);
        Assert.NotEqual(ServiceOwnershipOwnerIdentityState.Observed, observed.State);
        Assert.Equal(string.Empty, observed.OwningUserSid);
    }

    [Fact]
    public void A_substituted_installation_id_for_the_same_owner_is_refused()
    {
        ServiceOwnershipOwnerIdentityObservation observed =
            Interpret(Facts(observedInstallationId: SubstitutedInstallation));

        Assert.False(observed.IsObserved);
        Assert.NotEqual(ServiceOwnershipOwnerIdentityState.Observed, observed.State);
        Assert.Equal(string.Empty, observed.OwningUserSid);
    }

    [Fact]
    public void Substituting_both_halves_together_is_still_refused()
    {
        ServiceOwnershipOwnerIdentityObservation observed = Interpret(Facts(
            observedInstallationId: SubstitutedInstallation,
            observedInitiatingUserSid: SubstitutedSid));

        Assert.False(observed.IsObserved);
        Assert.Equal(string.Empty, observed.OwningUserSid);
    }

    // =======================================================================
    // 4. A MALFORMED OR NON-USER-SHAPED EXPECTED SID REFUSES
    // =======================================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-sid")]
    [InlineData("S-1-5-21")]
    [InlineData("NT AUTHORITY\\SYSTEM")]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-19")]
    [InlineData("S-1-5-11")]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-5-80-1111111111-2222222222-3333333333-1444444444-1234567890")]
    public void A_malformed_or_non_user_shaped_expected_sid_refuses(string sid)
    {
        // BOTH sides carry it, so the equality comparison passes and the ONLY
        // thing that can refuse is the shape rule itself.
        ServiceOwnershipOwnerIdentityObservation observed =
            Interpret(Facts(observedInitiatingUserSid: sid, expectedInitiatingUserSid: sid));

        Assert.False(observed.IsObserved);
        Assert.Equal(string.Empty, observed.OwningUserSid);
    }

    [Fact]
    public void An_anchor_that_produced_nothing_is_unavailable_rather_than_an_owner()
    {
        Assert.Equal(
            ServiceOwnershipOwnerIdentityState.Unavailable,
            Interpret(Facts(anchorValidated: false)).State);
        Assert.Equal(
            ServiceOwnershipOwnerIdentityState.Unavailable,
            Interpret(Facts(anchorAccepted: false)).State);
        Assert.Equal(
            ServiceOwnershipOwnerIdentityState.Unavailable,
            Interpret(Facts(documentPresent: false)).State);
        Assert.Equal(
            ServiceOwnershipOwnerIdentityState.Unavailable,
            Interpret(Facts(observedInitiatingUserSid: null)).State);
        Assert.Equal(
            ServiceOwnershipOwnerIdentityState.Unavailable,
            Interpret(Facts(observedInstallationId: null)).State);
    }

    [Fact]
    public void A_default_anchor_observation_never_reads_as_an_owner()
    {
        ServiceOwnershipOwnerIdentityObservation observed =
            ServiceOwnershipOwnerIdentityInterpreter.Interpret(default);

        Assert.False(observed.IsObserved);
        Assert.Equal(string.Empty, observed.OwningUserSid);
    }

    // =======================================================================
    // 5. THE PORT CANNOT BE CONSTRUCTED WITHOUT THE VALIDATED ANCHOR
    // =======================================================================

    [Fact]
    public void The_owner_port_takes_exactly_one_validated_installation_anchor_document()
    {
        ConstructorInfo bound = Assert.Single(
            typeof(ServiceOwnershipFixedOwnerIdentityPort).GetConstructors(AllInstance));

        ParameterInfo parameter = Assert.Single(bound.GetParameters());

        // NOT a SID string, NOT an installation-id string, NOT a pair of strings:
        // the ONE closed document the channel already validated.
        Assert.Equal(typeof(ServiceInstallationAnchorDocument), parameter.ParameterType);
        Assert.Equal("anchor", parameter.Name);
    }

    [Fact]
    public void The_owner_port_exposes_no_parameterless_constructor()
    {
        ConstructorInfo[] parameterless = typeof(ServiceOwnershipFixedOwnerIdentityPort)
            .GetConstructors(AllInstance)
            .Where(c => c.GetParameters().Length == 0)
            .ToArray();

        Assert.Empty(parameterless);
    }

    [Fact]
    public void The_owner_port_accepts_no_string_delegate_path_or_strategy()
    {
        foreach (ConstructorInfo constructor in
                 typeof(ServiceOwnershipFixedOwnerIdentityPort).GetConstructors(AllInstance))
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                Assert.NotEqual(typeof(string), parameter.ParameterType);
                Assert.False(typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
            }
        }
    }

    [Fact]
    public void The_owner_observation_entry_point_stays_parameterless()
    {
        MethodInfo observe = Assert.Single(
            typeof(ServiceOwnershipFixedOwnerIdentityPort)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == nameof(ServiceOwnershipFixedOwnerIdentityPort.ObserveFixedOwner)));

        Assert.Empty(observe.GetParameters());
    }

    // =======================================================================
    // 6. THE COMPOSITION ROOT BINDS THE PORT FROM context.Anchor
    // =======================================================================

    [Fact]
    public void The_composition_root_binds_the_owner_port_from_the_context_anchor()
    {
        string code = CompositionRootCode();

        Assert.Contains(
            "new ServiceOwnershipFixedOwnerIdentityPort(context.Anchor)", code, StringComparison.Ordinal);

        // ...and never from nothing at all.
        Assert.DoesNotContain(
            "new ServiceOwnershipFixedOwnerIdentityPort()", code, StringComparison.Ordinal);

        // POSITIVE CONTROL: the same scan really does fire on the unbound form.
        Assert.Contains(
            "new ServiceOwnershipFixedOwnerIdentityPort()",
            SetupCSharpLexicalScanner.ExtractCode(
                "class X { void M() { var p = new ServiceOwnershipFixedOwnerIdentityPort(); } }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_composition_root_requires_a_matching_pre_existing_anchor_before_it_constructs_anything()
    {
        string code = CompositionRootCode();

        // The three ordering facts Apply must require BEFORE it constructs a port.
        Assert.Contains("ServiceAnchorPresence.MatchingPresent", code, StringComparison.Ordinal);
        Assert.Contains("context.Presence", code, StringComparison.Ordinal);
        Assert.Contains("context.AnchorCreatedThisAttempt", code, StringComparison.Ordinal);

        // The anchor's own installation id is compared against the id AcceptPayload
        // cached from the already-validated anchor.
        Assert.Contains("context.Anchor.InstallationId", code, StringComparison.Ordinal);
        Assert.Contains("expectedInstallationOwnershipId", code, StringComparison.Ordinal);

        // And the anchor's SID is shape-checked before anything is built from it.
        Assert.Contains("context.Anchor.InitiatingUserSid", code, StringComparison.Ordinal);

        // The guard runs BEFORE the machine-data root is derived, so a refusal
        // cannot reach a single construction.
        int guard = code.IndexOf("ServiceAnchorPresence.MatchingPresent", StringComparison.Ordinal);
        int derive = code.IndexOf(
            "Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)",
            StringComparison.Ordinal);
        int construct = code.IndexOf(
            "new ServiceOwnershipFixedOwnerIdentityPort(", StringComparison.Ordinal);

        Assert.True(guard > 0, "the presence guard must exist in the composition root");
        Assert.True(derive > 0, "the machine-data derivation must exist in the composition root");
        Assert.True(construct > 0, "the owner port must be constructed in the composition root");
        Assert.True(guard < derive, "the presence guard must precede the store-root derivation");
        Assert.True(guard < construct, "the presence guard must precede every construction");
    }

    [Fact]
    public void The_context_is_no_longer_documented_as_deliberately_unused()
    {
        // The pre-cycle-96 root stated in its own comment that the context was
        // deliberately unused. That sentence and the behaviour it described are
        // both gone.
        string raw = File.ReadAllText(CompositionRootPath());
        Assert.DoesNotContain("context is deliberately UNUSED", raw, StringComparison.Ordinal);
    }

    // =======================================================================
    // 7. NO PAYLOAD OR CALLER STRING CAN INJECT THE EXPECTED SID
    // =======================================================================

    [Fact]
    public void The_accepted_payload_surface_carries_no_sid_of_any_kind()
    {
        string[] sidShapedNames = typeof(ServicePromotionRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n.Contains("Sid", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("Owner", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("Principal", StringComparison.OrdinalIgnoreCase)
                        || n.Contains("Account", StringComparison.OrdinalIgnoreCase))
            .Where(n => !n.Contains("InstallationOwnershipId", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(sidShapedNames);

        string[] depromotionSidShapedNames = typeof(ServiceDepromotionRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(n => n.Contains("Sid", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(depromotionSidShapedNames);
    }

    [Fact]
    public void Accept_payload_still_takes_only_the_payload_text_and_the_validated_installation_id()
    {
        MethodInfo accept = Assert.Single(
            typeof(ServiceOwnershipElevatedTransaction)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == nameof(ServiceOwnershipElevatedTransaction.AcceptPayload)));

        ParameterInfo[] parameters = accept.GetParameters();

        Assert.Equal(2, parameters.Length);
        Assert.Equal("payloadText", parameters[0].Name);
        Assert.Equal("anchorInstallationId", parameters[1].Name);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal(typeof(string), parameters[1].ParameterType);
    }

    [Fact]
    public void No_sid_token_can_travel_through_the_elevated_argument_vector()
    {
        string protocol = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipElevatedProtocol.cs")));

        foreach (string token in new[]
                 {
                     "clientSid", "ClientSid", "ownerSid", "OwnerSid", "expectedSid", "ExpectedSid",
                     "initiatingUserSid", "InitiatingUserSid", "--sid",
                 })
        {
            Assert.DoesNotContain(token, protocol, StringComparison.Ordinal);
        }

        // POSITIVE CONTROL for the same scan.
        Assert.Contains(
            "clientSid",
            SetupCSharpLexicalScanner.ExtractCode("class X { void M(string clientSid) { } }"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_owner_adapter_never_reads_a_caller_string_an_environment_value_or_a_process_identity()
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(OwnerAdapterPath()));

        foreach (string token in new[]
                 {
                     "GetEnvironmentVariable", "WindowsIdentity", "GetCurrent", "OpenProcessToken",
                     "GetTokenInformation", "Environment.UserName", "Environment.UserDomainName",
                     "Registry", "File.ReadAllText", "Path.Combine",
                 })
        {
            Assert.DoesNotContain(token, code, StringComparison.Ordinal);
        }

        // The ONE source it reads is the certified installation anchor store, and
        // that read is a REREAD of the anchor the constructor already fixed.
        Assert.Contains("ServiceInstallationAnchorStore.Read()", code, StringComparison.Ordinal);
    }

    // =======================================================================
    // 8. NO OTHER PRODUCTION FILE CONSTRUCTS THE OWNER PORT
    // =======================================================================

    [Fact]
    public void Exactly_one_production_file_constructs_the_owner_port_and_it_is_the_composition_root()
    {
        var constructing = new System.Collections.Generic.List<string>();
        int calibration = 0;

        foreach (string file in ProductionSources())
        {
            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(file));
            if (code.Contains("namespace ", StringComparison.Ordinal))
            {
                calibration++;
            }
            if (code.Contains("new ServiceOwnershipFixedOwnerIdentityPort", StringComparison.Ordinal))
            {
                constructing.Add(Path.GetFullPath(file));
            }
        }

        Assert.True(calibration > 100, "the positive control failed, so the result below is not calibrated");

        string only = Assert.Single(constructing);
        Assert.Equal(Path.GetFullPath(CompositionRootPath()), only, StringComparer.OrdinalIgnoreCase);
    }
}
