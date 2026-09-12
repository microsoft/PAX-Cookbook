using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 96 - THE DESCRIPTOR SNAPSHOT RACE BETWEEN STEP (f) AND STEP (h)
// ===========================================================================
//
// THE DEFECT THIS FILE WAS WRITTEN AGAINST. Step (f) CAPTURES the prior
// descriptor and the credential observation confirms it. Step (h) then applied
// the approved grant through a port that received ONLY the grant plan. That port
// took a FRESH, UNBOUND read of the current descriptor and built the approved
// bytes from whatever it found. If an administrator changed the descriptor
// between (f) and (h), the product happily granted on top of the CHANGED
// descriptor while the ledger still carried the restoration bytes captured at
// (f). Any later compensation then wrote those stale bytes back, silently
// destroying the administrator's change.
//
// HOW THE RACE IS MADE MECHANICALLY OBSERVABLE. Everything below is a PURE
// interpreter plus a FAKE descriptor surface that COUNTS write attempts. That
// counter is what turns "refused BEFORE any write" and "wrote, then restored a
// stale snapshot over it" into two different, directly measurable facts rather
// than two readings of the same outcome token.
//
// THE DECISION ALWAYS COMES FROM PRODUCTION. The fake port is a thin harness: it
// calls the REAL ServiceOwnershipApprovedDescriptorInterpreter to decide whether
// approved bytes may be produced at all, and only writes what production
// produced. The fake owns the counter and the descriptor cell; it owns no policy.
//
// NOTHING HERE opens a certificate store, reopens a private key, reads or writes
// a real ACL, reads or writes %ProgramData%, reads the registry, elevates,
// starts a process, opens a socket, runs PAX or starts a Bake.
public sealed class ServiceOwnershipDescriptorSnapshotTests
{
    // =======================================================================
    // TWO DIFFERENT, BOTH-SUPPORTED DESCRIPTORS
    // =======================================================================
    //
    // The certified supported shape is extremely rigid: owner BUILTIN\Administrators,
    // exactly two ACEs (SYSTEM then Administrators) with fixed flags and mask, and
    // no SACL. The ONE field a real administrator can change while the descriptor
    // stays supported is the GROUP SID, so descriptor B differs from descriptor A
    // in exactly the last sub-authority of the group SID.

    /// <summary>Offset of the low byte of the group SID's final sub-authority.</summary>
    private const int GroupSidFinalSubAuthorityOffset = 60;

    private static byte[] DescriptorA() =>
        (byte[])ServiceOwnershipPromotionFixtures.PriorBytes.Clone();

    private static byte[] DescriptorB()
    {
        byte[] changed = DescriptorA();
        changed[GroupSidFinalSubAuthorityOffset] = 0xEA; // RID 1001 -> 1002
        return changed;
    }

    [Fact]
    public void The_two_fixtures_really_are_two_different_supported_descriptors()
    {
        // Without this calibration every assertion below could be passing for the
        // wrong reason - for instance because B is simply malformed.
        byte[] a = DescriptorA();
        byte[] b = DescriptorB();

        Assert.NotEqual(a, b);
        Assert.Equal(a.Length, b.Length);

        Assert.True(ServiceOwnershipLedgerContract.TryParseFileSecurityDescriptor(
            a, out ServiceOwnershipParsedFileSecurityDescriptor? parsedA));
        Assert.True(ServiceOwnershipLedgerContract.TryParseFileSecurityDescriptor(
            b, out ServiceOwnershipParsedFileSecurityDescriptor? parsedB));

        Assert.True(ServiceOwnershipLedgerContract.IsSupportedPriorDescriptorShape(parsedA!, out _));
        Assert.True(ServiceOwnershipLedgerContract.IsSupportedPriorDescriptorShape(parsedB!, out _));

        // The change is exactly the group SID, and nothing else.
        Assert.NotEqual(parsedA!.GroupSid, parsedB!.GroupSid);
        Assert.Equal(parsedA.OwnerSid, parsedB.OwnerSid);
        Assert.Equal(parsedA.Aces.Count, parsedB.Aces.Count);

        // And grant(A) is not grant(B), so "which descriptor was granted on" is a
        // real, observable difference rather than a coincidence.
        Assert.True(ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            a, ServiceOwnershipPromotionFixtures.SvcSid, out byte[] grantA, out _, out _));
        Assert.True(ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            b, ServiceOwnershipPromotionFixtures.SvcSid, out byte[] grantB, out _, out _));
        Assert.NotEqual(grantA, grantB);
    }

    private static ServiceOwnershipCapturedPriorDescriptor CapturedOf(byte[] bytes)
    {
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            "present", out ServiceOwnershipPriorDaclState present));

        return ServiceOwnershipCapturedPriorDescriptor.Captured(
            present,
            Convert.ToBase64String(bytes),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
    }

    // =======================================================================
    // 1. THE PORT CONTRACT MUST CARRY THE CAPTURED DESCRIPTOR
    // =======================================================================

    [Fact]
    public void The_approved_descriptor_port_requires_the_captured_prior_descriptor()
    {
        MethodInfo apply = Assert.Single(
            typeof(IServiceOwnershipApprovedDescriptorPort).GetMethods());

        ParameterInfo[] parameters = apply.GetParameters();

        // ONE parameter means the port can only ever look at whatever the machine
        // happens to hold when it runs. TWO means it is bound to the snapshot that
        // step (f) captured and the credential observation confirmed.
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(ServiceOwnershipPromotionGrantPlan), parameters[0].ParameterType);
        Assert.Equal(typeof(ServiceOwnershipCapturedPriorDescriptor), parameters[1].ParameterType);
        Assert.Equal("captured", parameters[1].Name);
    }

    [Fact]
    public void No_overload_preserves_the_old_unbound_apply_signature()
    {
        MethodInfo[] unbound = typeof(IServiceOwnershipApprovedDescriptorPort)
            .GetMethods()
            .Where(m => m.GetParameters().Length == 1)
            .ToArray();

        Assert.Empty(unbound);

        MethodInfo[] portUnbound = typeof(ServiceOwnershipFixedApprovedDescriptorPort)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                        | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "ApplyApprovedDescriptor")
            .Where(m => m.GetParameters().Length != 2)
            .ToArray();

        Assert.Empty(portUnbound);
    }

    // =======================================================================
    // 2. THE FAKE DESCRIPTOR SURFACE - IT COUNTS WRITE ATTEMPTS
    // =======================================================================

    /// <summary>
    /// A synthetic stand-in for the ONE machine descriptor cell. It records how
    /// many times a native write was ATTEMPTED, which is the fact that separates
    /// "refused before writing" from "wrote and then restored a stale snapshot".
    /// </summary>
    private sealed class FakeDescriptorSurface
    {
        private byte[] current;

        internal FakeDescriptorSurface(byte[] initial) => current = (byte[])initial.Clone();

        /// <summary>Every write ATTEMPT, whether or not it changed anything.</summary>
        internal int WriteAttempts { get; private set; }

        internal byte[] Current => (byte[])current.Clone();

        /// <summary>
        /// The conditional write the fixed shim is required to perform: read the
        /// CURRENT descriptor through the same handle immediately before writing,
        /// and refuse without calling the write API when it is not what was
        /// expected.
        /// </summary>
        internal ServiceOwnershipObservedDescriptorWriteFacts WriteWhenCurrentMatches(
            byte[] expectedCurrent, byte[] descriptorBytes)
        {
            if (!current.AsSpan().SequenceEqual(expectedCurrent))
            {
                // Closed WITHOUT attempting a write. Nothing changed.
                return new ServiceOwnershipObservedDescriptorWriteFacts(
                    true, true, true, false, false, false, null);
            }

            WriteAttempts++;
            current = (byte[])descriptorBytes.Clone();
            return new ServiceOwnershipObservedDescriptorWriteFacts(
                true, true, true, true, true, true, (byte[])current.Clone());
        }

        /// <summary>The unconditional restore write, exactly as compensation performs it.</summary>
        internal ServiceOwnershipObservedDescriptorWriteFacts WriteUnconditionally(byte[] descriptorBytes)
        {
            WriteAttempts++;
            current = (byte[])descriptorBytes.Clone();
            return new ServiceOwnershipObservedDescriptorWriteFacts(
                true, true, true, true, true, true, (byte[])current.Clone());
        }
    }

    /// <summary>
    /// The harness port. It reproduces the production sequence EXACTLY - observe,
    /// let the production interpreter decide, then write - and adds nothing but a
    /// counter.
    /// </summary>
    private sealed class HarnessApprovedDescriptorPort : IServiceOwnershipApprovedDescriptorPort
    {
        private readonly FakeDescriptorSurface surface;

        internal HarnessApprovedDescriptorPort(FakeDescriptorSurface surface) => this.surface = surface;

        internal int Calls { get; private set; }

        /// <summary>Every captured descriptor OBJECT this port was handed, in order.</summary>
        internal List<ServiceOwnershipCapturedPriorDescriptor> ReceivedCaptured { get; } = new();

        public ServiceOwnershipDescriptorApplyState ApplyApprovedDescriptor(
            ServiceOwnershipPromotionGrantPlan plan,
            ServiceOwnershipCapturedPriorDescriptor captured)
        {
            Calls++;
            ReceivedCaptured.Add(captured);

            // (1) THE OBSERVATION. In production this is the fixed shim's read pass.
            byte[] observed = surface.Current;
            var facts = new ServiceOwnershipObservedKeyAccessFacts(
                true, true, false, true, false, true, observed);

            // (2) THE DECISION, TAKEN BY PRODUCTION CODE.
            if (!ServiceOwnershipApprovedDescriptorInterpreter.TryPlanApprovedDescriptor(
                    plan, captured, facts, out byte[] approved,
                    out ServiceOwnershipDescriptorApplyState refusal))
            {
                return refusal;
            }

            // (3) THE CONDITIONAL WRITE, then the verification read. The
            // precondition bytes are the CAPTURED ones, exactly as the fixed shim
            // now requires them on its own write handle.
            Assert.True(ServiceOwnershipApprovedDescriptorInterpreter.TryDecodeCapturedSnapshot(
                plan.Key, captured, out byte[] capturedBytes));

            return ServiceOwnershipApprovedDescriptorInterpreter.InterpretApplyOutcome(
                approved, surface.WriteWhenCurrentMatches(capturedBytes, approved));
        }
    }

    /// <summary>The restore port, writing exactly the captured bytes and nothing else.</summary>
    private sealed class HarnessDescriptorRestorePort : IServiceOwnershipDescriptorRestorePort
    {
        private readonly FakeDescriptorSurface surface;

        internal HarnessDescriptorRestorePort(FakeDescriptorSurface surface) => this.surface = surface;

        internal int Calls { get; private set; }

        public ServiceOwnershipDescriptorRestoreState RestoreCapturedDescriptor(
            ServiceOwnershipPromotionKeyHandle key,
            ServiceOwnershipCapturedPriorDescriptor captured)
        {
            Calls++;

            if (!ServiceOwnershipDescriptorRestoreInterpreter.TryPlanRestoration(
                    key, captured, out byte[] restoration,
                    out ServiceOwnershipDescriptorRestoreState refusal))
            {
                return refusal;
            }

            return ServiceOwnershipDescriptorRestoreInterpreter.InterpretRestoreOutcome(
                restoration, surface.WriteUnconditionally(restoration));
        }
    }

    // =======================================================================
    // 3. THE HARNESS IS FAITHFUL TO THE PRODUCTION PORT
    // =======================================================================

    [Fact]
    public void The_production_port_performs_the_same_observe_decide_write_sequence_this_harness_models()
    {
        string body = ApprovedDescriptorPortBody();

        int observe = body.IndexOf("ObserveKeyDescriptor(plan.Key)", StringComparison.Ordinal);
        int decide = body.IndexOf("TryPlanApprovedDescriptor(", StringComparison.Ordinal);
        int write = body.IndexOf("WriteKeyDescriptorWhenCurrentMatches(", StringComparison.Ordinal);
        int interpret = body.IndexOf("InterpretApplyOutcome(", StringComparison.Ordinal);

        Assert.True(observe >= 0, "the production port must observe the key descriptor");
        Assert.True(decide > observe, "the production port must decide after it observes");
        Assert.True(write > decide, "the production port must write only after it decides");
        Assert.True(interpret >= 0 && interpret < write, "the write result must be interpreted");

        // The captured snapshot reaches BOTH the decision and the write.
        Assert.Contains("plan, captured, observed", body, StringComparison.Ordinal);
        Assert.Contains("plan.Key, capturedBytes, approved", body, StringComparison.Ordinal);

        // POSITIVE CONTROL: the extraction really did isolate a non-empty body.
        Assert.True(body.Length > 200, "the port body extraction produced nothing to scan");
    }

    /// <summary>
    /// The approved-descriptor port's OWN body. Scanning the whole file would find
    /// the interpreter's DECLARATION of TryPlanApprovedDescriptor long before the
    /// port's call to it, which would make an ordering assertion meaningless.
    /// </summary>
    private static string ApprovedDescriptorPortBody()
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(Path.Combine(
            ServiceOwnershipElevatedTransactionStructuralTests.RepoRoot(),
            "src", "PAXCookbookSetup", "Service", "ServiceOwnershipFixedKeyDescriptorAdapters.cs")));

        int start = code.IndexOf(
            "class ServiceOwnershipFixedApprovedDescriptorPort", StringComparison.Ordinal);
        Assert.True(start > 0, "the approved-descriptor port must be declared in the adapters file");

        int end = code.IndexOf(
            "class ServiceOwnershipFixedDescriptorRestorePort", start, StringComparison.Ordinal);
        Assert.True(end > start, "the restore port must follow the approved-descriptor port");

        return code[start..end];
    }

    [Fact]
    public void The_restore_path_keeps_its_own_unconditional_write_and_the_grant_path_does_not()
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(Path.Combine(
            ServiceOwnershipElevatedTransactionStructuralTests.RepoRoot(),
            "src", "PAXCookbookSetup", "Service", "ServiceOwnershipFixedKeyDescriptorAdapters.cs")));

        // EXACTLY ONE conditional write entry point, invoked from EXACTLY ONE site.
        Assert.Equal(2, CountOccurrences(code, "WriteKeyDescriptorWhenCurrentMatches"));

        // EXACTLY ONE unconditional restore entry point, invoked from EXACTLY ONE site.
        Assert.Equal(2, CountOccurrences(code, "RestoreKeyDescriptor"));

        // The pre-cycle-96 unbound write entry point is gone entirely.
        Assert.DoesNotContain("WriteKeyDescriptor(", code, StringComparison.Ordinal);

        // ONE kernel write API call site, still owner/group/DACL only.
        Assert.Equal(1, CountOccurrences(code, "SetKernelObjectSecurity(handle"));

        // POSITIVE CONTROL for the same counter.
        Assert.Equal(
            1,
            CountOccurrences(
                SetupCSharpLexicalScanner.ExtractCode(
                    "class X { void M() { WriteKeyDescriptor(); } }"),
                "WriteKeyDescriptor("));
    }

    [Fact]
    public void The_conditional_write_reads_the_current_descriptor_on_the_same_handle_before_writing()
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(Path.Combine(
            ServiceOwnershipElevatedTransactionStructuralTests.RepoRoot(),
            "src", "PAXCookbookSetup", "Service", "ServiceOwnershipFixedKeyDescriptorAdapters.cs")));

        int start = code.IndexOf(
            "private static ServiceOwnershipObservedDescriptorWriteFacts WriteThroughFixedHandle",
            StringComparison.Ordinal);
        Assert.True(start > 0, "the one write sequence must exist");

        string body = code[start..];

        int open = body.IndexOf("OpenFixedTarget(", StringComparison.Ordinal);
        int identity = body.IndexOf("HandleResolvesToExpectedTarget(handle", StringComparison.Ordinal);
        int preRead = body.IndexOf("ReadOwnerGroupDacl(handle, buffer)", StringComparison.Ordinal);
        int compare = body.IndexOf("MatchesExactly(", StringComparison.Ordinal);
        int write = body.IndexOf("WriteOwnerGroupDacl(handle", StringComparison.Ordinal);

        Assert.True(open >= 0, "the write sequence must open the fixed target");
        Assert.True(identity > open, "the opened handle must be proven to be the fixed target");
        Assert.True(preRead > identity, "the precondition read must follow the identity proof");
        Assert.True(compare > preRead, "the comparison must follow the precondition read");
        Assert.True(write > compare, "the write must follow the comparison");

        // The precondition read and the write both name the SAME handle variable,
        // which is what makes "the same handle" a fact rather than a claim.
        Assert.Contains("ReadOwnerGroupDacl(handle,", body, StringComparison.Ordinal);
        Assert.Contains("WriteOwnerGroupDacl(handle,", body, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while (true)
        {
            index = haystack.IndexOf(needle, index, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }
            count++;
            index += needle.Length;
        }
    }

    // =======================================================================
    // 4. THE RACE - CAPTURED A, CURRENT B
    // =======================================================================

    private sealed class Scenario
    {
        internal Scenario(byte[] captured, byte[] current)
        {
            Surface = new FakeDescriptorSurface(current);
            Approved = new HarnessApprovedDescriptorPort(Surface);
            Restore = new HarnessDescriptorRestorePort(Surface);
            PriorDescriptor = new FakePriorDescriptorPort { Next = CapturedOf(captured) };
            CapturedBytes = (byte[])captured.Clone();
        }

        internal FakeDescriptorSurface Surface { get; }

        internal HarnessApprovedDescriptorPort Approved { get; }

        internal HarnessDescriptorRestorePort Restore { get; }

        internal FakePriorDescriptorPort PriorDescriptor { get; }

        internal byte[] CapturedBytes { get; }

        internal FakeOwnerIdentityPort Owner { get; } = new();

        internal FakeServiceSidPort ServiceSid { get; } = new();

        internal FakeCertificateFactsPort Certificate { get; } = new();

        internal FakeCredentialObservationPort Credential { get; } = new();

        internal FakeLedgerPersistencePort Ledger { get; } = new();

        internal FakePromotedRecipePort Recipe { get; } = new();

        internal ServiceOwnershipPromotionResult Promote()
        {
            var executor = new ServiceOwnershipPromotionExecutor(
                Owner, ServiceSid, Certificate, PriorDescriptor, Approved, Restore,
                Credential, Ledger, Recipe);

            return executor.Promote(
                ServiceOwnershipPromotionFixtures.PromotionRequest(),
                ServiceOwnershipLedgerValidator.ForAbsentLedger(),
                ServiceOwnershipPromotionFixtures.EntryId,
                ServiceOwnershipPromotionFixtures.Stamp);
        }
    }

    [Fact]
    public void A_descriptor_that_changed_after_capture_causes_zero_native_write_attempts()
    {
        // THE CORE PROPERTY. Step (f) captured A and the credential observation
        // confirmed A. By the time step (h) runs, the machine holds B. Not one
        // byte may be written to B.
        var scenario = new Scenario(captured: DescriptorA(), current: DescriptorB());
        scenario.Credential.Script(
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant);

        ServiceOwnershipPromotionResult result = scenario.Promote();

        Assert.Equal(0, scenario.Surface.WriteAttempts);
        Assert.Equal(DescriptorB(), scenario.Surface.Current);
        Assert.False(result.IsPromoted);
        Assert.Equal(
            ServiceOwnershipPromotionExecutorOutcome.DescriptorApplyRefused, result.TriggeringFailure);
    }

    [Fact]
    public void A_refusal_before_any_write_never_restores_the_stale_captured_snapshot()
    {
        // The transaction changed NOTHING, so there is nothing to put back, and
        // putting A back would destroy the administrator's change B.
        var scenario = new Scenario(captured: DescriptorA(), current: DescriptorB());
        scenario.Credential.Script(
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant);

        scenario.Promote();

        Assert.Equal(0, scenario.Restore.Calls);
        Assert.Equal(DescriptorB(), scenario.Surface.Current);
    }

    [Fact]
    public void A_later_failure_can_never_reach_a_compensation_that_overwrites_the_changed_descriptor()
    {
        // The historical failure mode, driven end to end: the grant is applied on
        // top of B, a later step fails, and compensation writes the stale A back.
        var scenario = new Scenario(captured: DescriptorA(), current: DescriptorB());
        scenario.Credential.Script(
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipCredentialObservation.Diverged);

        ServiceOwnershipPromotionResult result = scenario.Promote();

        Assert.False(result.IsPromoted);
        Assert.Equal(0, scenario.Surface.WriteAttempts);
        Assert.Equal(0, scenario.Restore.Calls);
        Assert.Equal(DescriptorB(), scenario.Surface.Current);
    }

    [Fact]
    public void The_executor_hands_the_port_the_exact_captured_object_from_step_f()
    {
        var scenario = new Scenario(captured: DescriptorA(), current: DescriptorA());
        scenario.Credential.Script(
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant);

        scenario.Promote();

        ServiceOwnershipCapturedPriorDescriptor handed =
            Assert.Single(scenario.Approved.ReceivedCaptured);

        // Two independent captures of the same bytes are equal by value, so only
        // the recorded Base64 and digest can prove it is the SAME snapshot the
        // prior-descriptor port produced at (f).
        Assert.Equal(scenario.PriorDescriptor.Next.BytesBase64, handed.BytesBase64);
        Assert.Equal(scenario.PriorDescriptor.Next.Sha256, handed.Sha256);
        Assert.Equal(scenario.PriorDescriptor.Next.State, handed.State);
    }

    // =======================================================================
    // 5. THE HONEST PATH STILL WORKS - CAPTURED A, CURRENT A
    // =======================================================================

    [Fact]
    public void An_unchanged_descriptor_still_applies_the_grant_built_from_the_captured_bytes()
    {
        var scenario = new Scenario(captured: DescriptorA(), current: DescriptorA());
        scenario.Credential.Script(
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant);

        ServiceOwnershipPromotionResult result = scenario.Promote();

        Assert.True(result.IsPromoted);
        Assert.Equal(1, scenario.Surface.WriteAttempts);
        Assert.Equal(0, scenario.Restore.Calls);

        Assert.True(ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            DescriptorA(), ServiceOwnershipPromotionFixtures.SvcSid,
            out byte[] expectedGrant, out _, out _));
        Assert.Equal(expectedGrant, scenario.Surface.Current);
    }

    [Fact]
    public void A_failure_after_an_actual_write_still_restores_exactly_the_captured_bytes()
    {
        // When a write DID happen, compensation must put back exactly A - and this
        // path is untouched by the snapshot precondition.
        var scenario = new Scenario(captured: DescriptorA(), current: DescriptorA());
        scenario.Credential.Script(
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipCredentialObservation.Diverged);

        ServiceOwnershipPromotionResult result = scenario.Promote();

        Assert.False(result.IsPromoted);
        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.RolledBack, result.Outcome);
        Assert.Equal(
            ServiceOwnershipPromotionExecutorOutcome.GrantObservationMismatch, result.TriggeringFailure);
        Assert.True(result.PriorDescriptorRestored);

        Assert.Equal(1, scenario.Restore.Calls);
        Assert.Equal(2, scenario.Surface.WriteAttempts); // the grant, then the restore
        Assert.Equal(DescriptorA(), scenario.Surface.Current);
    }

    [Fact]
    public void Post_write_verification_still_requires_exact_equality()
    {
        // A verification read that does not match the approved bytes EXACTLY can
        // never report Applied, no matter how close it is.
        Assert.True(ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            DescriptorA(), ServiceOwnershipPromotionFixtures.SvcSid,
            out byte[] approved, out _, out _));

        byte[] nearlyRight = (byte[])approved.Clone();
        nearlyRight[^1] ^= 0x01;

        Assert.Equal(
            ServiceOwnershipDescriptorApplyState.Failed,
            ServiceOwnershipApprovedDescriptorInterpreter.InterpretApplyOutcome(
                approved,
                new ServiceOwnershipObservedDescriptorWriteFacts(
                    true, true, true, true, true, true, nearlyRight)));

        Assert.Equal(
            ServiceOwnershipDescriptorApplyState.Applied,
            ServiceOwnershipApprovedDescriptorInterpreter.InterpretApplyOutcome(
                approved,
                new ServiceOwnershipObservedDescriptorWriteFacts(
                    true, true, true, true, true, true, approved)));
    }

    [Fact]
    public void A_write_that_was_never_attempted_is_a_refusal_and_never_a_failure_to_verify()
    {
        Assert.True(ServiceOwnershipLedgerContract.TryBuildPostGrantDescriptor(
            DescriptorA(), ServiceOwnershipPromotionFixtures.SvcSid,
            out byte[] approved, out _, out _));

        Assert.Equal(
            ServiceOwnershipDescriptorApplyState.Refused,
            ServiceOwnershipApprovedDescriptorInterpreter.InterpretApplyOutcome(
                approved,
                new ServiceOwnershipObservedDescriptorWriteFacts(
                    true, true, true, false, false, false, null)));
    }
}
