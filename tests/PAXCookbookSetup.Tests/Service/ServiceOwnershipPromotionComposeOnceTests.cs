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
// CYCLE 90 PASS A - COMPOSE-ONCE, THE CERTIFIED-ENTRY OBSERVATION PORT, AND
// THE AUTHORIZED REFUSAL ORDER
// ===========================================================================
//
// WHY THIS FILE EXISTS. Cycle 89 halted because step (f) had to observe a
// credential against a validated ServiceOwnershipLedgerEntry that Setup cannot
// construct. Brian authorized Option A: compose Preparing + Intended ONCE
// through the certified transition authority BEFORE the first observation, hand
// THAT certified entry to the observation port, and persist the SAME composed
// serialization at (g).
//
// THE AUTHORIZED ORDERING CHANGE THIS FILE PINS. Composition now happens BEFORE
// the first credential observation, so when the composition AND the credential
// state would both be invalid, TransitionRefused WINS - there is no certified
// entry to observe. That is a deliberate, authorized change of which refusal
// wins, and every assertion below states it explicitly rather than inferring it.
//
// SCOPE. Every effect is a fake port or a source-text scan. Nothing here opens a
// certificate store, touches a private key, reads or writes an ACL, reads the
// registry, installs or starts a service, elevates, writes %ProgramData%, opens
// a socket, starts a process, or contacts any machine.
public sealed class ServiceOwnershipPromotionComposeOnceTests
{
    private readonly FakeOwnerIdentityPort owner = new();
    private readonly FakeServiceSidPort serviceSid = new();
    private readonly FakeCertificateFactsPort certificate = new();
    private readonly FakePriorDescriptorPort priorDescriptor = new();
    private readonly FakeApprovedDescriptorPort apply = new();
    private readonly FakeDescriptorRestorePort restore = new();
    private readonly FakeCredentialObservationPort credential = new();
    private readonly FakeLedgerPersistencePort ledger = new();
    private readonly FakePromotedRecipePort recipe = new();

    private ServiceOwnershipPromotionExecutor Executor() =>
        new(owner, serviceSid, certificate, priorDescriptor, apply, restore, credential,
            ledger, recipe);

    private ServiceOwnershipPromotionResult Promote(string? stamp = null) =>
        Executor().Promote(
            ServiceOwnershipPromotionFixtures.PromotionRequest(),
            ServiceOwnershipLedgerValidator.ForAbsentLedger(),
            ServiceOwnershipPromotionFixtures.EntryId,
            stamp ?? ServiceOwnershipPromotionFixtures.Stamp);

    /// <summary>
    /// A CAPTURED descriptor whose recorded bytes cannot survive
    /// ServiceOwnershipTransitionFacts.TryCreate. The capture itself SUCCEEDS, so
    /// the sequence is past (f)'s descriptor gate and the ONLY thing that can
    /// refuse is the composition.
    /// </summary>
    private static ServiceOwnershipCapturedPriorDescriptor UncomposableCapture()
    {
        Assert.True(ServiceOwnershipLedgerContract.TryParseWireToken(
            "present", out ServiceOwnershipPriorDaclState present));

        return ServiceOwnershipCapturedPriorDescriptor.Captured(
            present, "this is not base64 and never will be", "NOT-A-SHA-256");
    }

    // =======================================================================
    // THE CONTRACT SHAPE
    // =======================================================================

    private static Assembly SetupAssembly() => typeof(ServiceOwnershipPromotionExecutor).Assembly;

    private static Type ObservationPortType() =>
        SetupAssembly().GetType("PAXCookbookSetup.Service.IServiceOwnershipCredentialObservationPort", throwOnError: true)!;

    private static Type PersistencePortType() =>
        SetupAssembly().GetType("PAXCookbookSetup.Service.IServiceOwnershipLedgerPersistencePort", throwOnError: true)!;

    [Fact]
    public void The_credential_observation_port_accepts_only_a_certified_ledger_entry()
    {
        MethodInfo method = Assert.Single(ObservationPortType().GetMethods());
        Assert.Equal("ObserveCredential", method.Name);

        ParameterInfo parameter = Assert.Single(method.GetParameters());
        Assert.Equal(typeof(ServiceOwnershipLedgerEntry), parameter.ParameterType);
        Assert.Equal(typeof(ServiceOwnershipCredentialObservation), method.ReturnType);

        // NEGATIVE CONTROL: the port must not be reachable with raw material a
        // caller could fabricate - no path, SID, provider name or descriptor byte.
        Assert.NotEqual(typeof(string), parameter.ParameterType);
        Assert.NotEqual(typeof(byte[]), parameter.ParameterType);
        Assert.False(typeof(Delegate).IsAssignableFrom(parameter.ParameterType));
    }

    // =======================================================================
    // THE PRODUCTION OBSERVATION ADAPTER
    // =======================================================================

    private static Type ObservationAdapterType()
    {
        Type port = ObservationPortType();

        Type[] implementations = SetupAssembly()
            .GetTypes()
            .Where(t => !t.IsInterface && !t.IsAbstract && port.IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();

        return Assert.Single(implementations);
    }

    [Fact]
    public void Exactly_one_production_adapter_implements_the_observation_port()
    {
        Type adapter = ObservationAdapterType();

        Assert.Equal("PAXCookbookSetup.Service", adapter.Namespace);
        Assert.False(adapter.IsPublic, adapter.Name + " must not be public");
        Assert.True(adapter.IsSealed, adapter.Name + " must be sealed");
    }

    [Fact]
    public void The_observation_adapter_is_declared_inside_the_certified_observer_file()
    {
        Type adapter = ObservationAdapterType();
        string declaration = "class " + adapter.Name;

        string[] declaringFiles = SourceFiles(Path.Combine(RepoRoot(), "src"))
            .Where(f => SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(f))
                .Contains(declaration, StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "ServiceOwnershipCredentialObserver.cs" }, declaringFiles);
    }

    [Fact]
    public void The_observation_adapter_holds_no_ledger_persistence_capability()
    {
        Type adapter = ObservationAdapterType();
        Type persistence = PersistencePortType();

        const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;

        var offenders = new List<string>();

        foreach (FieldInfo field in adapter.GetFields(All))
        {
            if (persistence.IsAssignableFrom(field.FieldType))
            {
                offenders.Add("field " + field.Name);
            }
        }

        foreach (PropertyInfo property in adapter.GetProperties(All))
        {
            if (persistence.IsAssignableFrom(property.PropertyType))
            {
                offenders.Add("property " + property.Name);
            }
        }

        foreach (ConstructorInfo constructor in adapter.GetConstructors(All))
        {
            foreach (ParameterInfo parameter in constructor.GetParameters())
            {
                if (persistence.IsAssignableFrom(parameter.ParameterType))
                {
                    offenders.Add("constructor parameter " + parameter.Name);
                }
            }
        }

        foreach (MethodInfo method in adapter.GetMethods(All))
        {
            if (persistence.IsAssignableFrom(method.ReturnType))
            {
                offenders.Add("return of " + method.Name);
            }
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                if (persistence.IsAssignableFrom(parameter.ParameterType))
                {
                    offenders.Add(method.Name + " parameter " + parameter.Name);
                }
            }
        }

        Assert.Empty(offenders);

        // POSITIVE CONTROL: the SAME predicate DOES fire on a type that genuinely
        // carries the persistence port, so the empty result above is calibrated.
        Assert.Contains(persistence, typeof(ContainedLedgerPersistencePort).GetInterfaces());
    }

    [Fact]
    public void The_observation_adapter_carries_no_state_and_no_injectable_seam()
    {
        Type adapter = ObservationAdapterType();

        const BindingFlags All =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;

        foreach (FieldInfo field in adapter.GetFields(All))
        {
            Assert.True(field.IsLiteral || field.IsInitOnly, adapter.Name + "." + field.Name + " is mutable");
            Assert.False(typeof(Delegate).IsAssignableFrom(field.FieldType), adapter.Name + "." + field.Name + " is a delegate");
        }

        foreach (MethodInfo method in adapter.GetMethods(All))
        {
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Assert.False(
                    typeof(Delegate).IsAssignableFrom(parameter.ParameterType),
                    adapter.Name + "." + method.Name + " accepts a delegate");
                Assert.NotEqual(typeof(string), parameter.ParameterType);
                Assert.NotEqual(typeof(byte[]), parameter.ParameterType);
            }
        }
    }

    // =======================================================================
    // COMPOSE ONCE - STRUCTURAL
    // =======================================================================

    private static string RepoRoot() => ServiceSidResolverStructuralTests.RepoRoot();

    private static string ExecutorPath() => Path.Combine(
        RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipPromotionExecutor.cs");

    private static string ObserverPath() => Path.Combine(
        RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipCredentialObserver.cs");

    private static string ExecutorCode() =>
        SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(ExecutorPath()));

    private static string[] SourceFiles(string root) =>
        Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

    internal static int Occurrences(string haystack, string needle)
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
    public void The_executor_names_the_begin_promotion_intent_operation_exactly_once()
    {
        string code = ExecutorCode();

        Assert.Equal(1, Occurrences(code, "ServiceOwnershipTransitionOperation.BeginPromotionIntent"));

        // ...and there is exactly ONE authority call site and ONE persistence call
        // site in the whole file, so "compose once" cannot be defeated by a second
        // hidden route.
        Assert.Equal(1, Occurrences(code, "ServiceOwnershipTransitionAuthority.Apply("));
        Assert.Equal(1, Occurrences(code, "ledger.Persist("));

        // POSITIVE CONTROL: the counter really counts.
        Assert.Equal(
            2,
            Occurrences(
                "ServiceOwnershipTransitionAuthority.Apply(a); ServiceOwnershipTransitionAuthority.Apply(b);",
                "ServiceOwnershipTransitionAuthority.Apply("));
    }

    /// <summary>The COMPILED BODY of PromoteCore, comments stripped.</summary>
    internal static string PromoteCoreBody()
    {
        string code = ExecutorCode();

        int start = code.IndexOf("PromoteCore(", StringComparison.Ordinal);
        Assert.True(start >= 0, "PromoteCore was not found");
        start = code.IndexOf("PromoteCore(", start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0, "the PromoteCore DEFINITION was not found");

        int end = code.IndexOf("private static", start, StringComparison.Ordinal);
        Assert.True(end > start, "the end of PromoteCore was not found");

        return code[start..end];
    }

    [Fact]
    public void The_intent_is_composed_once_before_the_first_observation_and_that_same_composition_is_persisted()
    {
        string body = PromoteCoreBody();

        int capture = body.IndexOf("CapturePriorDescriptor", StringComparison.Ordinal);
        int compose = body.IndexOf("TryComposeBeginPromotionIntent", StringComparison.Ordinal);
        int persist = body.IndexOf("TryPersistComposedTransition", StringComparison.Ordinal);
        int grant = body.IndexOf("ApplyApprovedDescriptor", StringComparison.Ordinal);

        foreach (int position in new[] { capture, compose, persist, grant })
        {
            Assert.True(position >= 0, "a required Option-A step is missing from PromoteCore");
        }

        // The composition happens exactly ONCE in the body, and the already-composed
        // result is persisted exactly ONCE.
        Assert.Equal(1, Occurrences(body, "TryComposeBeginPromotionIntent"));
        Assert.Equal(1, Occurrences(body, "TryPersistComposedTransition"));

        var observations = new List<int>();
        for (int i = body.IndexOf("ObserveCredential", StringComparison.Ordinal);
             i >= 0;
             i = body.IndexOf("ObserveCredential", i + 1, StringComparison.Ordinal))
        {
            observations.Add(i);
        }

        Assert.Equal(3, observations.Count);

        // capture < compose-once < first observation < persist-that-composition < grant
        Assert.True(capture < compose, "the prior descriptor must be captured before the intent is composed");
        Assert.True(compose < observations[0], "the intent must be COMPOSED before the first observation");
        Assert.True(observations[0] < persist, "the first observation must precede persistence of that composition");
        Assert.True(persist < grant, "the composed intent must be persisted before the grant is applied");

        // POSITIVE CONTROL. The SAME predicates applied to the SUPERSEDED cycle-88
        // order - observe first, then compose and persist together - must FAIL.
        const string SupersededOrder =
            "CapturePriorDescriptor ObserveCredential TryComposeBeginPromotionIntent "
            + "TryPersistComposedTransition ApplyApprovedDescriptor";

        int oldCapture = SupersededOrder.IndexOf("CapturePriorDescriptor", StringComparison.Ordinal);
        int oldCompose = SupersededOrder.IndexOf("TryComposeBeginPromotionIntent", StringComparison.Ordinal);
        int oldObservation = SupersededOrder.IndexOf("ObserveCredential", StringComparison.Ordinal);

        Assert.True(oldCapture < oldCompose, "the control string is calibrated for the capture");
        Assert.False(
            oldCompose < oldObservation,
            "the superseded order observed the credential BEFORE the intent was ever composed");
    }

    // =======================================================================
    // THE AUTHORIZED REFUSAL ORDER
    // =======================================================================

    [Fact]
    public void An_uncapturable_prior_descriptor_refuses_before_any_composition_or_observation()
    {
        priorDescriptor.Next = ServiceOwnershipCapturedPriorDescriptor.Failure(
            ServiceOwnershipPriorDescriptorState.Unavailable);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.PriorDescriptorUnavailable, result.Outcome);
        Assert.Equal(5, result.CompletedSteps);
        Assert.Equal(0, credential.Calls);
        Assert.Empty(ledger.PersistedGenerations);
        Assert.Equal(0, apply.Calls);
    }

    [Fact]
    public void A_composition_that_cannot_produce_facts_refuses_before_the_credential_is_ever_observed()
    {
        priorDescriptor.Next = UncomposableCapture();
        credential.Script(ServiceOwnershipCredentialObservation.MatchesCapturedPriorState);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.TransitionRefused, result.Outcome);
        Assert.Equal(5, result.CompletedSteps);
        Assert.Equal(0, credential.Calls);
        Assert.Empty(ledger.PersistedGenerations);
        Assert.Equal(0, apply.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-08-06T00:00:00.000Z")]
    [InlineData("nonsense")]
    public void A_composition_the_authority_refuses_stops_before_the_credential_is_ever_observed(string stamp)
    {
        credential.Script(ServiceOwnershipCredentialObservation.MatchesCapturedPriorState);

        ServiceOwnershipPromotionResult result = Promote(stamp);

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.TransitionRefused, result.Outcome);
        Assert.Equal(5, result.CompletedSteps);
        Assert.Equal(0, credential.Calls);
        Assert.Empty(ledger.PersistedGenerations);
        Assert.Equal(0, apply.Calls);
    }

    /// <summary>
    /// THE AUTHORIZED PRECEDENCE. When the composition AND the credential state
    /// would BOTH be invalid, TransitionRefused wins - because no certified entry
    /// exists to observe.
    /// </summary>
    [Theory]
    [InlineData(ServiceOwnershipCredentialObservation.Unavailable)]
    [InlineData(ServiceOwnershipCredentialObservation.Diverged)]
    [InlineData(ServiceOwnershipCredentialObservation.KeyIdentityMismatch)]
    public void A_transition_refusal_outranks_a_credential_mismatch_when_both_would_fail(
        ServiceOwnershipCredentialObservation observation)
    {
        priorDescriptor.Next = UncomposableCapture();
        credential.Script(observation);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.TransitionRefused, result.Outcome);
        Assert.NotEqual(
            ServiceOwnershipPromotionExecutorOutcome.PreconditionObservationMismatch, result.Outcome);
        Assert.Equal(5, result.CompletedSteps);
        Assert.Equal(0, credential.Calls);
        Assert.Empty(ledger.PersistedGenerations);
    }

    [Theory]
    [InlineData(ServiceOwnershipCredentialObservation.Unavailable)]
    [InlineData(ServiceOwnershipCredentialObservation.Diverged)]
    [InlineData(ServiceOwnershipCredentialObservation.KeyIdentityMismatch)]
    [InlineData(ServiceOwnershipCredentialObservation.MatchesRecordedGrant)]
    public void A_valid_composition_with_a_mismatched_credential_persists_nothing(
        ServiceOwnershipCredentialObservation observation)
    {
        credential.Script(observation);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(
            ServiceOwnershipPromotionExecutorOutcome.PreconditionObservationMismatch, result.Outcome);
        Assert.Equal(5, result.CompletedSteps);
        Assert.Equal(1, credential.Calls);
        Assert.Empty(ledger.PersistedGenerations);
        Assert.Equal(0, apply.Calls);
        Assert.Equal(0, restore.Calls);
        Assert.Equal(0, recipe.PersistCalls);
    }

    [Fact]
    public void A_failure_to_persist_the_already_composed_intent_reports_six_completed_steps()
    {
        credential.Script(ServiceOwnershipCredentialObservation.MatchesCapturedPriorState);
        ledger.Script(ServiceOwnershipLedgerPersistState.Refused);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.LedgerPersistenceRefused, result.Outcome);
        Assert.Equal(6, result.CompletedSteps);
        Assert.Equal(1, credential.Calls);
        Assert.Equal(1, ledger.PersistedGenerations.Count);
        Assert.Equal(0, apply.Calls);
    }

    /// <summary>
    /// NO ACL MUTATION IS REACHABLE before a successful Preparing + Intended
    /// persistence, at ANY of the refusal points above.
    /// </summary>
    [Fact]
    public void No_descriptor_is_ever_applied_before_the_intent_is_durably_persisted()
    {
        // 1. prior descriptor unavailable
        priorDescriptor.Next = ServiceOwnershipCapturedPriorDescriptor.Failure(
            ServiceOwnershipPriorDescriptorState.Unavailable);
        Assert.Equal(
            ServiceOwnershipPromotionExecutorOutcome.PriorDescriptorUnavailable, Promote().Outcome);
        Assert.Equal(0, apply.Calls);

        // 2. composition refused
        var second = new ServiceOwnershipPromotionComposeOnceTests();
        second.priorDescriptor.Next = UncomposableCapture();
        second.Promote();
        Assert.Equal(0, second.apply.Calls);

        // 3. credential mismatch
        var third = new ServiceOwnershipPromotionComposeOnceTests();
        third.credential.Script(ServiceOwnershipCredentialObservation.Diverged);
        third.Promote();
        Assert.Equal(0, third.apply.Calls);

        // 4. persistence of the composed intent refused
        var fourth = new ServiceOwnershipPromotionComposeOnceTests();
        fourth.credential.Script(ServiceOwnershipCredentialObservation.MatchesCapturedPriorState);
        fourth.ledger.Script(ServiceOwnershipLedgerPersistState.Refused);
        fourth.Promote();
        Assert.Equal(0, fourth.apply.Calls);

        // POSITIVE CONTROL: a clean run DOES reach the apply, so the four zeros
        // above are not simply an unreachable port.
        var clean = new ServiceOwnershipPromotionComposeOnceTests();
        clean.credential.Script(
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant);
        Assert.True(clean.Promote().IsPromoted);
        Assert.Equal(1, clean.apply.Calls);
    }

    // =======================================================================
    // COMPOSE ONCE - BEHAVIOURAL, PROVEN BY OBJECT IDENTITY
    // =======================================================================

    private FakeCredentialObservationPort HealthyObservations() =>
        credential.Script(
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant);

    [Fact]
    public void The_entry_observed_before_persistence_is_the_object_the_persisted_state_carries_forward()
    {
        HealthyObservations();

        Assert.True(Promote().IsPromoted);
        Assert.Equal(3, credential.ObservedEntries.Count);

        // THE COMPOSE-ONCE PROOF. The entry observed at (f) - before anything was
        // persisted - is the SAME OBJECT as the entry taken from the state that (g)
        // returned. Had (g) invoked the authority a second time instead of
        // persisting what (f) already composed, it would have produced a fresh
        // accepted document and this would be a different instance.
        Assert.Same(credential.ObservedEntries[0], credential.ObservedEntries[1]);

        // The final observation legitimately comes from a LATER composition
        // (Done + Active), so it must NOT be the same object.
        Assert.NotSame(credential.ObservedEntries[1], credential.ObservedEntries[2]);

        // POSITIVE CONTROL: reference identity is a DISCRIMINATING instrument here.
        // Two independent compositions of the same PURE transition produce
        // byte-identical output but DIFFERENT objects, so Assert.Same above could
        // not have passed by accident.
        ServiceOwnershipLedgerEntry first =
            ServiceOwnershipPromotionFixtures.BeginIntent().Accepted!.Document!.Entries[0];
        ServiceOwnershipLedgerEntry second =
            ServiceOwnershipPromotionFixtures.BeginIntent().Accepted!.Document!.Entries[0];

        Assert.NotSame(first, second);
        Assert.Equal(first.EntryId, second.EntryId);
    }

    [Fact]
    public void The_entry_handed_to_the_first_observation_is_the_requested_preparing_intended_entry()
    {
        HealthyObservations();

        Assert.True(Promote().IsPromoted);

        ServiceOwnershipLedgerEntry observed = credential.ObservedEntries[0];

        Assert.Equal(ServiceOwnershipPromotionFixtures.EntryId, observed.EntryId);
        Assert.Equal(ServiceOwnershipLifecycleState.Intended, observed.LifecycleState);
        Assert.Equal(
            ServiceOwnershipPromotionFixtures.JobId,
            Assert.Single(observed.AssociatedPromotedJobIds));
        Assert.Equal(ServiceOwnershipPromotionFixtures.KeyId, observed.KeyIdentity);
        Assert.Equal(ServiceOwnershipPromotionFixtures.PriorSha256, observed.PriorDaclSha256);
    }

    [Fact]
    public void The_bytes_persisted_at_step_g_are_the_composition_the_first_observation_was_judged_against()
    {
        HealthyObservations();

        Assert.True(Promote().IsPromoted);

        // Three persists, three DISTINCT serialization objects - no serialization is
        // ever handed to the port twice.
        Assert.Equal(3, ledger.PersistedSerializations.Count);
        Assert.NotSame(ledger.PersistedSerializations[0], ledger.PersistedSerializations[1]);
        Assert.NotSame(ledger.PersistedSerializations[1], ledger.PersistedSerializations[2]);
        Assert.NotSame(ledger.PersistedSerializations[0], ledger.PersistedSerializations[2]);

        Assert.Equal(1, ledger.PersistedGenerations[0]);
        Assert.Equal(ServiceOwnershipLedgerOutcome.InProgress, ledger.PersistedOutcomes[0]);

        // The FIRST persisted bytes carry exactly the entry the FIRST observation
        // was judged against, field for field.
        ServiceOwnershipLedgerValidationResult roundTrip =
            ServiceOwnershipLedgerValidator.Validate(ledger.PersistedSerializations[0].Json);
        Assert.True(roundTrip.IsAccepted);

        ServiceOwnershipLedgerEntry persisted = Assert.Single(roundTrip.Document!.Entries);
        ServiceOwnershipLedgerEntry observed = credential.ObservedEntries[0];

        Assert.Equal(observed.EntryId, persisted.EntryId);
        Assert.Equal(observed.LifecycleState, persisted.LifecycleState);
        Assert.Equal(ServiceOwnershipLifecycleState.Intended, persisted.LifecycleState);
        Assert.Equal(observed.KeyIdentity, persisted.KeyIdentity);
        Assert.Equal(observed.ProviderUniqueName, persisted.ProviderUniqueName);
        Assert.Equal(observed.CertificateThumbprintSha1, persisted.CertificateThumbprintSha1);
        Assert.Equal(observed.ServiceSid, persisted.ServiceSid);
        Assert.Equal(observed.OwningUserSid, persisted.OwningUserSid);
        Assert.Equal(observed.PriorDaclBytesBase64, persisted.PriorDaclBytesBase64);
        Assert.Equal(observed.PriorDaclSha256, persisted.PriorDaclSha256);
        Assert.Equal(observed.AssociatedPromotedJobIds, persisted.AssociatedPromotedJobIds);
        Assert.Equal(ServiceOwnershipTransactionState.Preparing, roundTrip.Document.TransactionState);
    }

    [Fact]
    public void A_refused_composition_hands_no_entry_to_the_observation_port_at_all()
    {
        priorDescriptor.Next = UncomposableCapture();
        credential.Script(ServiceOwnershipCredentialObservation.MatchesCapturedPriorState);

        Assert.Equal(
            ServiceOwnershipPromotionExecutorOutcome.TransitionRefused, Promote().Outcome);

        Assert.Empty(credential.ObservedEntries);
        Assert.Empty(ledger.PersistedSerializations);
    }

    // =======================================================================
    // NO JSON SYNTHESIS, NO VISIBILITY WIDENING
    // =======================================================================

    [Fact]
    public void Neither_the_executor_nor_the_observer_synthesizes_ledger_json()
    {
        string[] markers =
        {
            "\"schemaVersion\"", "\"installationOwnershipId\"", "\"entries\"",
            "\"lifecycleState\"", "\"transactionState\"", "\"priorDaclBytesBase64\"",
        };

        foreach (string path in new[] { ExecutorPath(), ObserverPath() })
        {
            string raw = File.ReadAllText(path);
            foreach (string marker in markers)
            {
                Assert.DoesNotContain(marker, raw, StringComparison.Ordinal);
            }
        }

        // POSITIVE CONTROL: the certified authority - the ONE place composition is
        // allowed to happen - does contain them, so the zeros above are calibrated.
        string authority = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "PAXCookbook.Shared", "Contracts", "ServiceOwnershipTransitionAuthority.cs"));

        int present = markers.Count(m => authority.Contains(m, StringComparison.Ordinal));
        Assert.True(present >= 4, "the JSON-synthesis scan is not calibrated: " + present);
    }

    [Fact]
    public void Nothing_in_the_observation_chain_widened_its_visibility()
    {
        Type observer = SetupAssembly().GetType(
            "PAXCookbookSetup.Service.ServiceOwnershipCredentialObserver", throwOnError: true)!;

        Assert.False(observer.IsPublic, "the observer shim must stay assembly-internal");
        Assert.False(ObservationPortType().IsPublic, "the observation port must stay assembly-internal");
        Assert.False(PersistencePortType().IsPublic, "the persistence port must stay assembly-internal");

        MethodInfo observe = Assert.Single(
            observer.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsPrivate)
                .ToArray());
        Assert.Equal("Observe", observe.Name);
        Assert.True(observe.IsAssembly, "the one shim entry point must stay internal");
        Assert.Equal(typeof(ServiceOwnershipLedgerEntry), Assert.Single(observe.GetParameters()).ParameterType);

        // The certified entry is STILL not constructible outside Shared: exactly one
        // constructor, and none of them public. Option A did not open a factory.
        ConstructorInfo[] all = typeof(ServiceOwnershipLedgerEntry)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Single(all);
        Assert.Empty(typeof(ServiceOwnershipLedgerEntry).GetConstructors());

        // POSITIVE CONTROL: the same reflection DOES see a public constructor where
        // one exists.
        Assert.NotEmpty(typeof(ServiceOwnershipPromotionComposeOnceTests).GetConstructors());
    }
}
