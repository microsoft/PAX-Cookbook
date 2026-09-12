using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using PAXCookbook.Shared.Contracts;
using PAXCookbookSetup.Service;
using Xunit;

namespace PAXCookbookSetup.Tests.Service;

// ===========================================================================
// CYCLE 88 - THE DETERMINISTIC PROMOTION / DEPROMOTION EXECUTOR
// ===========================================================================
//
// SCOPE. Every effect is a FAKE port. The one test that reaches disk does so
// inside a FRESH OS-TEMP CONTAINMENT ROOT it creates and deletes. Nothing here
// opens a certificate store, touches a private key, reads or writes an ACL,
// reads the registry, installs or starts a service, elevates, writes
// %ProgramData%, opens a socket, starts a process, or contacts any machine.
public sealed class ServiceOwnershipPromotionExecutorTests : IDisposable
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

    private readonly List<string> temporaryRoots = new();

    public void Dispose()
    {
        foreach (string root in temporaryRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception)
            {
                // A stranded temp directory is harmless.
            }
        }
    }

    private ServiceOwnershipPromotionExecutor Executor(
        IServiceOwnershipLedgerPersistencePort? ledgerPort = null) =>
        new(owner, serviceSid, certificate, priorDescriptor, apply, restore, credential,
            ledgerPort ?? ledger, recipe);

    /// <summary>The three observations a clean promotion makes, in order.</summary>
    private FakeCredentialObservationPort HealthyObservations() =>
        credential.Script(
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant);

    private ServiceOwnershipPromotionResult Promote(
        IServiceOwnershipLedgerPersistencePort? ledgerPort = null) =>
        Executor(ledgerPort).Promote(
            ServiceOwnershipPromotionFixtures.PromotionRequest(),
            ServiceOwnershipLedgerValidator.ForAbsentLedger(),
            ServiceOwnershipPromotionFixtures.EntryId,
            ServiceOwnershipPromotionFixtures.Stamp);

    // =======================================================================
    // THE ORDERED SEQUENCE
    // =======================================================================

    [Fact]
    public void A_clean_promotion_reaches_the_final_step_and_only_then_reports_success()
    {
        HealthyObservations();

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.Promoted, result.Outcome);
        Assert.True(result.IsPromoted);
        Assert.Equal(ServiceOwnershipPromotionExecutor.TotalPromotionSteps, result.CompletedSteps);

        // Every port was reached exactly as many times as the sequence prescribes.
        Assert.Equal(1, owner.Calls);
        Assert.Equal(1, serviceSid.Calls);
        Assert.Equal(1, certificate.Calls);
        Assert.Equal(1, priorDescriptor.Calls);
        Assert.Equal(1, apply.Calls);
        Assert.Equal(1, recipe.PersistCalls);
        Assert.Equal(3, credential.Calls);
        Assert.Equal(0, restore.Calls);
        Assert.Equal(0, recipe.CompensateCalls);
    }

    [Fact]
    public void The_ledger_is_persisted_three_times_with_a_strictly_advancing_generation()
    {
        HealthyObservations();

        Assert.True(Promote().IsPromoted);

        Assert.Equal(new[] { 1, 2, 3 }, ledger.PersistedGenerations.ToArray());
        Assert.Equal(
            new[]
            {
                ServiceOwnershipLedgerOutcome.InProgress,
                ServiceOwnershipLedgerOutcome.InProgress,
                ServiceOwnershipLedgerOutcome.Active,
            },
            ledger.PersistedOutcomes.ToArray());
    }

    [Fact]
    public void The_intent_is_committed_before_the_descriptor_is_ever_applied()
    {
        HealthyObservations();
        ledger.Script(ServiceOwnershipLedgerPersistState.Refused);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.LedgerPersistenceRefused, result.Outcome);

        // Nothing was changed, so nothing had to be unwound.
        Assert.Equal(0, apply.Calls);
        Assert.Equal(0, recipe.PersistCalls);
        Assert.Equal(0, restore.Calls);
    }

    [Fact]
    public void The_certificate_port_is_asked_only_about_the_thumbprint_the_request_names()
    {
        HealthyObservations();

        Assert.True(Promote().IsPromoted);

        Assert.Equal(ServiceOwnershipPromotionFixtures.Thumb, certificate.LastThumbprint);
    }

    // =======================================================================
    // THE DETERMINISTIC FAILPOINT MATRIX
    // =======================================================================
    //
    // The lettering below is the AUTHORIZED cycle-88 phase sequence:
    //   a request, b ledger, c owner, d SID, e certificate, f prior descriptor,
    //   g Preparing+Intended, h apply grant, i require grant, j CredentialMutated,
    //   k promoted Recipe, l Done+Active, m final grant, n success.
    //
    // THE THREE DURABLE INTERMEDIATE STATES, and what a restart must tolerate at
    // each under THIS order:
    //   Preparing + Intended (written at g) - the grant MAY be applied, because h
    //       and i both run while this is the only thing on disk. The Recipe NEVER
    //       exists here. Covered by the h, i and j failpoints below.
    //   CredentialMutated + Intended (written at j) - the grant IS applied AND was
    //       PROVEN at i. The Recipe MAY exist. Covered by the k failpoint.
    //   Done + Active (written at l) - the grant is applied and the Recipe exists.
    //       Covered by the m failpoint, which walks the full restore chain.
    // Compensation is unconditional at every one of them, so CompensatePromotedRecipe
    // must tolerate a Recipe that was never written; the h failpoint proves it does.

    [Fact]
    public void An_unavailable_owner_identity_stops_at_step_c()
    {
        owner.Next = ServiceOwnershipOwnerIdentityObservation.Failure(
            ServiceOwnershipOwnerIdentityState.Unavailable);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.OwnerIdentityUnavailable, result.Outcome);
        Assert.Equal(2, result.CompletedSteps);
        Assert.Equal(0, serviceSid.Calls);
        Assert.Empty(ledger.PersistedGenerations);
    }

    // The failure vocabularies are INTERNAL types, so the theory parameters are
    // their integer values: an xUnit theory method must be public, and a public
    // signature cannot name an internal enum.
    [Theory]
    [InlineData((int)ServiceOwnershipServiceSidState.Unresolved)]
    [InlineData((int)ServiceOwnershipServiceSidState.NotServiceShaped)]
    public void An_unresolved_service_sid_stops_at_step_d(int state)
    {
        serviceSid.Next = ServiceOwnershipServiceSidObservation.Failure(
            (ServiceOwnershipServiceSidState)state);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.ServiceSidUnavailable, result.Outcome);
        Assert.Equal(3, result.CompletedSteps);
        Assert.Equal(0, certificate.Calls);
        Assert.Empty(ledger.PersistedGenerations);
    }

    [Theory]
    [InlineData((int)ServiceOwnershipCertificateFactsState.NotFound)]
    [InlineData((int)ServiceOwnershipCertificateFactsState.Ambiguous)]
    [InlineData((int)ServiceOwnershipCertificateFactsState.PrivateKeyUnavailable)]
    [InlineData((int)ServiceOwnershipCertificateFactsState.UnsupportedProvider)]
    public void Unusable_certificate_facts_stop_at_step_e(int state)
    {
        certificate.Next = ServiceOwnershipCertificateFacts.Failure(
            (ServiceOwnershipCertificateFactsState)state);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.CertificateFactsUnavailable, result.Outcome);
        Assert.Equal(4, result.CompletedSteps);
        Assert.Equal(0, priorDescriptor.Calls);
        Assert.Empty(ledger.PersistedGenerations);
    }

    [Theory]
    [InlineData((int)ServiceOwnershipPriorDescriptorState.Unavailable)]
    [InlineData((int)ServiceOwnershipPriorDescriptorState.UnsupportedShape)]
    [InlineData((int)ServiceOwnershipPriorDescriptorState.ServiceAceAlreadyPresent)]
    public void An_uncapturable_prior_descriptor_stops_at_step_f(int state)
    {
        priorDescriptor.Next = ServiceOwnershipCapturedPriorDescriptor.Failure(
            (ServiceOwnershipPriorDescriptorState)state);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.PriorDescriptorUnavailable, result.Outcome);
        Assert.Equal(5, result.CompletedSteps);
        Assert.Equal(0, credential.Calls);
        Assert.Empty(ledger.PersistedGenerations);
    }

    [Theory]
    [InlineData(ServiceOwnershipCredentialObservation.Unavailable)]
    [InlineData(ServiceOwnershipCredentialObservation.Diverged)]
    [InlineData(ServiceOwnershipCredentialObservation.KeyIdentityMismatch)]
    [InlineData(ServiceOwnershipCredentialObservation.MatchesRecordedGrant)]
    public void A_credential_that_is_not_the_captured_prior_state_stops_at_step_f(
        ServiceOwnershipCredentialObservation observation)
    {
        credential.Script(observation);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(
            ServiceOwnershipPromotionExecutorOutcome.PreconditionObservationMismatch, result.Outcome);
        Assert.Equal(5, result.CompletedSteps);
        Assert.Equal(0, apply.Calls);
        Assert.Empty(ledger.PersistedGenerations);
    }

    [Theory]
    [InlineData((int)ServiceOwnershipDescriptorApplyState.Refused)]
    [InlineData((int)ServiceOwnershipDescriptorApplyState.Failed)]
    public void A_refused_grant_unwinds_at_step_h_without_the_credential_ever_changing(int state)
    {
        HealthyObservations();
        apply.Next = (ServiceOwnershipDescriptorApplyState)state;

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.RolledBack, result.Outcome);
        Assert.Equal(
            ServiceOwnershipPromotionExecutorOutcome.DescriptorApplyRefused, result.TriggeringFailure);
        Assert.Equal(7, result.CompletedSteps);

        // The grant never took, so there is nothing to put back - and that counts
        // as a PROVEN restore rather than an unprovable one.
        Assert.Equal(0, restore.Calls);
        Assert.True(result.PriorDescriptorRestored);
        Assert.False(result.EntryMarkedStale);

        // The Recipe now lives at (k), so it was never written.
        Assert.Equal(0, recipe.PersistCalls);
        Assert.Equal(1, recipe.CompensateCalls);
    }

    /// <summary>
    /// THE WINDOW THE PHASE ORDER OPENS ON PURPOSE. The grant WAS applied at (h)
    /// but (i) cannot prove it, so the unwind has to undo a credential change that
    /// the ledger never recorded. Durable state at that moment is Preparing +
    /// Intended, which is exactly what (g) already promised to cover.
    /// </summary>
    [Fact]
    public void An_unprovable_grant_at_step_i_unwinds_an_applied_but_unrecorded_credential()
    {
        credential.Script(
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipCredentialObservation.Diverged);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.RolledBack, result.Outcome);
        Assert.Equal(
            ServiceOwnershipPromotionExecutorOutcome.GrantObservationMismatch, result.TriggeringFailure);
        Assert.Equal(8, result.CompletedSteps);

        // Applied, then put back and PROVEN back.
        Assert.Equal(1, apply.Calls);
        Assert.Equal(1, restore.Calls);
        Assert.True(result.PriorDescriptorRestored);

        // CredentialMutated was NEVER recorded: only the intent and the compensating
        // removal ever reached the ledger.
        Assert.Equal(new[] { 1, 2 }, ledger.PersistedGenerations.ToArray());
        Assert.Equal(ServiceOwnershipLedgerOutcome.ValidEmpty, ledger.PersistedOutcomes[^1]);

        // The Recipe lives at (k), past the failure, so it was never written.
        Assert.Equal(0, recipe.PersistCalls);
        Assert.Equal(1, recipe.CompensateCalls);
    }

    /// <summary>
    /// A failure recording the PROVEN grant at (j). The credential is already
    /// changed, so the unwind must put it back even though the ledger still says
    /// Preparing.
    /// </summary>
    [Fact]
    public void A_ledger_failure_at_step_j_still_puts_the_applied_credential_back()
    {
        HealthyObservations();
        ledger.Script(
            ServiceOwnershipLedgerPersistState.Persisted,
            ServiceOwnershipLedgerPersistState.Refused);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.RolledBack, result.Outcome);
        Assert.Equal(
            ServiceOwnershipPromotionExecutorOutcome.LedgerPersistenceRefused, result.TriggeringFailure);
        Assert.Equal(9, result.CompletedSteps);

        Assert.Equal(1, apply.Calls);
        Assert.Equal(1, restore.Calls);
        Assert.True(result.PriorDescriptorRestored);
        Assert.Equal(0, recipe.PersistCalls);
        Assert.Equal(1, recipe.CompensateCalls);
    }

    /// <summary>
    /// The Recipe now lands at (k), so a refusal there unwinds a state in which the
    /// grant IS applied AND CredentialMutated IS recorded.
    /// </summary>
    [Fact]
    public void A_refused_promoted_recipe_unwinds_an_applied_and_recorded_grant()
    {
        HealthyObservations();
        recipe.NextPersist = ServiceOwnershipPromotedRecipePortOutcome.Refused;

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.RolledBack, result.Outcome);
        Assert.Equal(
            ServiceOwnershipPromotionExecutorOutcome.PromotedRecipeRefused, result.TriggeringFailure);
        Assert.Equal(10, result.CompletedSteps);

        Assert.Equal(1, apply.Calls);
        Assert.Equal(1, restore.Calls);
        Assert.True(result.PriorDescriptorRestored);
        Assert.False(result.EntryMarkedStale);
        Assert.Equal(1, recipe.CompensateCalls);

        // Intent, mutation, then the compensating removal.
        Assert.Equal(new[] { 1, 2, 3 }, ledger.PersistedGenerations.ToArray());
        Assert.Equal(ServiceOwnershipLedgerOutcome.ValidEmpty, ledger.PersistedOutcomes[^1]);
    }

    /// <summary>
    /// THE LOAD-BEARING CASE. Everything through step (l) succeeds and only the
    /// FINAL observation at step (m) fails. Success must NOT be reported.
    /// </summary>
    [Fact]
    public void A_failure_at_the_final_observation_is_never_reported_as_success()
    {
        credential.Script(
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant,
            ServiceOwnershipCredentialObservation.Diverged);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.NotEqual(ServiceOwnershipPromotionExecutorOutcome.Promoted, result.Outcome);
        Assert.False(result.IsPromoted);
        Assert.Equal(
            ServiceOwnershipPromotionExecutorOutcome.GrantObservationMismatch, result.TriggeringFailure);
        Assert.Equal(12, result.CompletedSteps);
        Assert.NotEqual(ServiceOwnershipPromotionExecutor.TotalPromotionSteps, result.CompletedSteps);

        // The transaction did not complete, so it unwound - and because the entry
        // had already reached Active, the unwind walked the restore chain rather
        // than dropping a record of a grant that once existed.
        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.RolledBack, result.Outcome);
        Assert.Equal(1, restore.Calls);
        Assert.Equal(1, recipe.PersistCalls);
        Assert.Equal(1, recipe.CompensateCalls);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, ledger.PersistedGenerations.ToArray());
        Assert.Equal(ServiceOwnershipLedgerOutcome.ValidEmpty, ledger.PersistedOutcomes[^1]);
    }

    [Theory]
    [InlineData((int)ServiceOwnershipDescriptorRestoreState.NotProven)]
    [InlineData((int)ServiceOwnershipDescriptorRestoreState.Failed)]
    public void An_unprovable_restore_marks_the_entry_stale_and_removes_nothing(int state)
    {
        credential.Script(
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipCredentialObservation.Diverged);
        restore.Next = (ServiceOwnershipDescriptorRestoreState)state;

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.RecoveryRequired, result.Outcome);
        Assert.False(result.PriorDescriptorRestored);
        Assert.True(result.EntryMarkedStale);

        // The LAST thing persisted is a stale ledger, never an empty one.
        Assert.Equal(ServiceOwnershipLedgerOutcome.Stale, ledger.PersistedOutcomes[^1]);
    }

    [Fact]
    public void A_ledger_persistence_failure_during_the_unwind_reports_recovery_required()
    {
        credential.Script(
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipCredentialObservation.Diverged);

        // The intent persists; the compensating removal does not. Under the phase
        // order the failure lands at (i), so CredentialMutated never gets written
        // and the unwind has exactly ONE persist to attempt.
        ledger.Script(
            ServiceOwnershipLedgerPersistState.Persisted,
            ServiceOwnershipLedgerPersistState.RecoveryRequired);

        ServiceOwnershipPromotionResult result = Promote();

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.RecoveryRequired, result.Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad entry id")]
    public void An_unbounded_entry_id_is_refused_before_any_port_is_touched(string? entryId)
    {
        ServiceOwnershipPromotionResult result = Executor().Promote(
            ServiceOwnershipPromotionFixtures.PromotionRequest(),
            ServiceOwnershipLedgerValidator.ForAbsentLedger(),
            entryId,
            ServiceOwnershipPromotionFixtures.Stamp);

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.InvalidRequest, result.Outcome);
        Assert.Equal(0, result.CompletedSteps);
        Assert.Equal(0, owner.Calls);
    }

    [Fact]
    public void A_null_request_or_ledger_is_refused_without_throwing()
    {
        Assert.Equal(
            ServiceOwnershipPromotionExecutorOutcome.InvalidRequest,
            Executor().Promote(
                null, ServiceOwnershipLedgerValidator.ForAbsentLedger(),
                ServiceOwnershipPromotionFixtures.EntryId,
                ServiceOwnershipPromotionFixtures.Stamp).Outcome);

        Assert.Equal(
            ServiceOwnershipPromotionExecutorOutcome.InvalidRequest,
            Executor().Promote(
                ServiceOwnershipPromotionFixtures.PromotionRequest(), null,
                ServiceOwnershipPromotionFixtures.EntryId,
                ServiceOwnershipPromotionFixtures.Stamp).Outcome);

        Assert.Equal(0, owner.Calls);
    }

    [Fact]
    public void A_ledger_that_already_holds_entries_can_never_start_a_new_promotion()
    {
        ServiceOwnershipPromotionResult result = Executor().Promote(
            ServiceOwnershipPromotionFixtures.PromotionRequest(),
            ServiceOwnershipPromotionFixtures.ActiveLedger(),
            ServiceOwnershipPromotionFixtures.EntryId,
            ServiceOwnershipPromotionFixtures.Stamp);

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.InvalidRequest, result.Outcome);
        Assert.Equal(0, owner.Calls);
    }

    /// <summary>
    /// CYCLE 90 PASS A. A non-canonical timestamp cannot compose, and composition
    /// now happens BEFORE the first credential observation - so this refuses as
    /// TransitionRefused at CompletedSteps 5, with the credential never observed,
    /// instead of the cycle-88 LedgerPersistenceRefused. That is the authorized
    /// ordering change, not a regression: nothing durable and no ACL is reachable
    /// in either version.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("2026-08-06T00:00:00.000Z")]
    [InlineData("nonsense")]
    public void A_non_canonical_timestamp_refuses_at_the_composition_before_any_observation(string stamp)
    {
        HealthyObservations();

        ServiceOwnershipPromotionResult result = Executor().Promote(
            ServiceOwnershipPromotionFixtures.PromotionRequest(),
            ServiceOwnershipLedgerValidator.ForAbsentLedger(),
            ServiceOwnershipPromotionFixtures.EntryId,
            stamp);

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.TransitionRefused, result.Outcome);
        Assert.Equal(5, result.CompletedSteps);
        Assert.Equal(0, credential.Calls);
        Assert.Empty(ledger.PersistedGenerations);
        Assert.Equal(0, apply.Calls);
    }

    // =======================================================================
    // END TO END AGAINST THE REAL DURABLE WRITE SURFACE
    // =======================================================================

    [Fact]
    public void A_clean_promotion_leaves_exactly_one_verified_active_ledger_on_disk()
    {
        string root = NewContainmentRoot();
        string serviceDirectory = Path.Combine(
            root,
            ServiceMachineStorageContract.MachineRootFolderName,
            ServiceMachineStorageContract.ServiceDataFolderName);
        Directory.CreateDirectory(serviceDirectory);

        HealthyObservations();
        var port = new ContainedLedgerPersistencePort(root);

        ServiceOwnershipPromotionResult result = Promote(port);

        Assert.True(result.IsPromoted, "end-to-end promotion: " + result.Outcome);
        Assert.Equal(3, port.Calls);

        // EXACTLY ONE file, and it is the ledger. No staging, no backup, no debris.
        string only = Assert.Single(Directory.GetFileSystemEntries(serviceDirectory));
        Assert.Equal(
            ServiceMachineStorageContract.OwnershipLedgerFileName,
            Path.GetFileName(only),
            StringComparer.Ordinal);

        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        ServiceOwnershipLedgerValidationResult onDisk =
            ServiceOwnershipLedgerValidator.Validate(strict.GetString(File.ReadAllBytes(only)));

        Assert.True(onDisk.IsAccepted);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Active, onDisk.Outcome);
        Assert.Equal(3, onDisk.Document!.Generation);

        ServiceOwnershipLedgerEntry entry = Assert.Single(onDisk.Document.Entries);
        Assert.Equal(ServiceOwnershipLifecycleState.Active, entry.LifecycleState);

        // THE RESTORE PAYLOAD SURVIVED THE WHOLE TRANSACTION.
        Assert.Equal(ServiceOwnershipPromotionFixtures.PriorB64, entry.PriorDaclBytesBase64);
        Assert.Equal(ServiceOwnershipPromotionFixtures.PriorSha256, entry.PriorDaclSha256);
    }

    [Fact]
    public void A_failed_promotion_leaves_a_stale_but_complete_ledger_on_disk()
    {
        string root = NewContainmentRoot();
        Directory.CreateDirectory(Path.Combine(
            root,
            ServiceMachineStorageContract.MachineRootFolderName,
            ServiceMachineStorageContract.ServiceDataFolderName));

        credential.Script(
            ServiceOwnershipCredentialObservation.MatchesCapturedPriorState,
            ServiceOwnershipCredentialObservation.Diverged);
        restore.Next = ServiceOwnershipDescriptorRestoreState.NotProven;

        ServiceOwnershipPromotionResult result = Promote(new ContainedLedgerPersistencePort(root));

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.RecoveryRequired, result.Outcome);
        Assert.True(result.EntryMarkedStale);

        string ledgerPath = Path.Combine(
            root,
            ServiceMachineStorageContract.MachineRootFolderName,
            ServiceMachineStorageContract.ServiceDataFolderName,
            ServiceMachineStorageContract.OwnershipLedgerFileName);

        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        ServiceOwnershipLedgerValidationResult onDisk =
            ServiceOwnershipLedgerValidator.Validate(strict.GetString(File.ReadAllBytes(ledgerPath)));

        Assert.True(onDisk.IsAccepted);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Stale, onDisk.Outcome);

        // NOTHING WAS REAPED. The bytes a human needs to finish the job by hand are
        // still there, and the entry is still there to point at them.
        ServiceOwnershipLedgerEntry entry = Assert.Single(onDisk.Document!.Entries);
        Assert.Equal(ServiceOwnershipLifecycleState.Stale, entry.LifecycleState);
        Assert.Equal(ServiceOwnershipPromotionFixtures.PriorB64, entry.PriorDaclBytesBase64);
        Assert.Equal(ServiceOwnershipPromotionFixtures.PriorSha256, entry.PriorDaclSha256);
        Assert.Single(entry.AssociatedPromotedJobIds);
    }

    [Fact]
    public void A_missing_machine_directory_is_never_created_by_the_transaction()
    {
        string root = NewContainmentRoot();

        HealthyObservations();

        ServiceOwnershipPromotionResult result = Promote(new ContainedLedgerPersistencePort(root));

        Assert.Equal(ServiceOwnershipPromotionExecutorOutcome.LedgerPersistenceRefused, result.Outcome);
        Assert.False(Directory.Exists(Path.Combine(
            root, ServiceMachineStorageContract.MachineRootFolderName)));
        Assert.Equal(0, apply.Calls);
    }

    private string NewContainmentRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "pax88-executor-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        temporaryRoots.Add(root);
        return root;
    }

    // =======================================================================
    // DEPROMOTION AND RECOVERY - PLANNER ACTIONS ONLY
    // =======================================================================

    private ServiceOwnershipLifecyclePlan RestorePlan(
        ServiceOwnershipLedgerValidationResult ledgerResult,
        ServiceOwnershipCredentialObservation observation)
    {
        ServiceOwnershipLifecyclePlanRequest? request =
            ServiceOwnershipLifecyclePlanRequest.ForRestoreCredential(
                ledgerResult,
                ServiceOwnershipPromotionFixtures.InstallId,
                ServiceOwnershipPromotionFixtures.OwnerSid,
                ServiceOwnershipPromotionFixtures.SvcSid,
                ServiceOwnershipPromotionFixtures.EntryId,
                observation);

        Assert.NotNull(request);
        return ServiceOwnershipLifecyclePlanner.Plan(request);
    }

    private ServiceOwnershipDepromotionResult Execute(ServiceOwnershipLifecyclePlan? plan) =>
        Executor().ExecutePlan(
            plan,
            ServiceOwnershipPromotionFixtures.ActiveLedger(),
            ServiceOwnershipPromotionFixtures.KeyHandle(),
            ServiceOwnershipPromotionFixtures.Captured(),
            ServiceOwnershipPromotionFixtures.EntryId,
            ServiceOwnershipPromotionFixtures.JobId,
            ServiceOwnershipPromotionFixtures.OperationId,
            ServiceOwnershipPromotionFixtures.Stamp);

    [Fact]
    public void A_restore_plan_is_executed_action_by_action_in_the_planners_own_order()
    {
        ServiceOwnershipLifecyclePlan plan = RestorePlan(
            ServiceOwnershipPromotionFixtures.ActiveLedger(),
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant);

        Assert.Equal(ServiceOwnershipPlanOutcome.PlanAvailable, plan.Outcome);
        Assert.NotEmpty(plan.Actions);

        ServiceOwnershipDepromotionResult result = Execute(plan);

        Assert.Equal(ServiceOwnershipDepromotionExecutorOutcome.Depromoted, result.Outcome);
        Assert.Equal(plan.Actions, result.ExecutedActions);
        Assert.True(restore.Calls >= 1, "the captured descriptor must actually be put back");
    }

    [Fact]
    public void A_restore_that_cannot_be_proven_marks_the_entry_stale_instead_of_removing_it()
    {
        restore.Next = ServiceOwnershipDescriptorRestoreState.NotProven;

        ServiceOwnershipDepromotionResult result = Execute(RestorePlan(
            ServiceOwnershipPromotionFixtures.ActiveLedger(),
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant));

        Assert.Equal(ServiceOwnershipDepromotionExecutorOutcome.MarkedStale, result.Outcome);
        Assert.Contains(ServiceOwnershipPlanAction.MarkStale, result.ExecutedActions);
        Assert.DoesNotContain(ServiceOwnershipPlanAction.RemoveRestoredEntry, result.ExecutedActions);
        Assert.Equal(ServiceOwnershipLedgerOutcome.Stale, ledger.PersistedOutcomes[^1]);
    }

    [Fact]
    public void A_refused_plan_executes_nothing()
    {
        ServiceOwnershipLifecyclePlan plan = RestorePlan(
            ServiceOwnershipPromotionFixtures.ActiveLedger(),
            ServiceOwnershipCredentialObservation.Unavailable);

        Assert.Equal(ServiceOwnershipPlanOutcome.Refused, plan.Outcome);

        ServiceOwnershipDepromotionResult result = Execute(plan);

        Assert.Equal(ServiceOwnershipDepromotionExecutorOutcome.PlanRefused, result.Outcome);
        Assert.Empty(result.ExecutedActions);
        Assert.Equal(0, restore.Calls);
        Assert.Empty(ledger.PersistedGenerations);
    }

    [Fact]
    public void A_null_plan_executes_nothing()
    {
        ServiceOwnershipDepromotionResult result = Execute(null);

        Assert.Equal(ServiceOwnershipDepromotionExecutorOutcome.InvalidRequest, result.Outcome);
        Assert.Empty(result.ExecutedActions);
        Assert.Equal(0, restore.Calls);
    }

    [Fact]
    public void A_ledger_persistence_failure_during_a_plan_stops_the_remaining_actions()
    {
        ledger.Script(ServiceOwnershipLedgerPersistState.Refused);

        ServiceOwnershipDepromotionResult result = Execute(RestorePlan(
            ServiceOwnershipPromotionFixtures.ActiveLedger(),
            ServiceOwnershipCredentialObservation.MatchesRecordedGrant));

        Assert.Equal(
            ServiceOwnershipDepromotionExecutorOutcome.LedgerPersistenceRefused, result.Outcome);
        Assert.Equal(1, ledger.PersistedGenerations.Count);
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

    private static string ExecutorPath() => Path.Combine(
        RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipPromotionExecutor.cs");

    /// <summary>The COMPILED BODY of PromoteCore, comments stripped.</summary>
    private static string PromoteCoreBody()
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(ExecutorPath()));

        int start = code.IndexOf("PromoteCore(", StringComparison.Ordinal);
        Assert.True(start >= 0, "PromoteCore was not found");
        start = code.IndexOf("PromoteCore(", start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0, "the PromoteCore DEFINITION was not found");

        int end = code.IndexOf("private static", start, StringComparison.Ordinal);
        Assert.True(end > start, "the end of PromoteCore was not found");

        return code[start..end];
    }

    /// <summary>
    /// THE ORDER IS THE AUTHORIZED PHASE ORDER, PINNED IN THE SOURCE ITSELF. This
    /// is a STRUCTURAL check on the compiled body, not on the comment lettering, so
    /// renumbering a comment can never make it pass. It exists because the first
    /// pass at this file shipped a plausible but UNAUTHORIZED order.
    ///
    /// CYCLE 90 PASS A STRENGTHENED IT. The intent is now COMPOSED once before the
    /// first observation and the SAME composition is persisted afterwards, so the
    /// chain this pins is:
    ///   capture &lt; compose-once &lt; first observation &lt; persist-that-composition
    ///   &lt; apply grant &lt; second observation &lt; CredentialMutated &lt; Recipe
    ///   &lt; Done+Active &lt; final observation.
    /// </summary>
    [Fact]
    public void The_promotion_body_performs_its_effects_in_the_authorized_phase_order()
    {
        string body = PromoteCoreBody();

        int owner = body.IndexOf("ObserveFixedOwner", StringComparison.Ordinal);
        int sid = body.IndexOf("ObserveFixedServiceSid", StringComparison.Ordinal);
        int certificate = body.IndexOf("ObserveCertificateFacts", StringComparison.Ordinal);
        int prior = body.IndexOf("CapturePriorDescriptor", StringComparison.Ordinal);
        int compose = body.IndexOf("TryComposeBeginPromotionIntent", StringComparison.Ordinal);
        int intent = body.IndexOf("TryPersistComposedTransition", StringComparison.Ordinal);
        int grant = body.IndexOf("ApplyApprovedDescriptor", StringComparison.Ordinal);
        int mutated = body.IndexOf("RecordCredentialMutated", StringComparison.Ordinal);
        int promotedRecipe = body.IndexOf("PersistPromotedRecipe", StringComparison.Ordinal);
        int complete = body.IndexOf("CompletePromotion", StringComparison.Ordinal);

        foreach (int position in new[]
                 { owner, sid, certificate, prior, compose, intent, grant, mutated, promotedRecipe, complete })
        {
            Assert.True(position >= 0, "an authorized step is missing from PromoteCore");
        }

        // c < d < e < f < g < h < j < k < l.
        Assert.True(owner < sid, "c owner identity must precede d service SID");
        Assert.True(sid < certificate, "d service SID must precede e certificate");
        Assert.True(certificate < prior, "e certificate must precede f prior descriptor");
        Assert.True(prior < compose, "f prior descriptor must precede the intent composition");
        Assert.True(compose < intent, "the intent must be COMPOSED before it is PERSISTED");
        Assert.True(intent < grant, "g Preparing+Intended must precede h apply grant");
        Assert.True(grant < mutated, "h apply grant must precede j CredentialMutated");
        Assert.True(mutated < promotedRecipe, "j CredentialMutated must precede k promoted Recipe");
        Assert.True(promotedRecipe < complete, "k promoted Recipe must precede l Done+Active");

        // The composition and the persistence of that composition each happen ONCE.
        Assert.Equal(1, ServiceOwnershipPromotionComposeOnceTests.Occurrences(body, "TryComposeBeginPromotionIntent"));
        Assert.Equal(1, ServiceOwnershipPromotionComposeOnceTests.Occurrences(body, "TryPersistComposedTransition"));

        // i sits BETWEEN h and j: the grant is PROVEN before it is RECORDED.
        var observations = new List<int>();
        for (int i = body.IndexOf("ObserveCredential", StringComparison.Ordinal);
             i >= 0;
             i = body.IndexOf("ObserveCredential", i + 1, StringComparison.Ordinal))
        {
            observations.Add(i);
        }

        Assert.Equal(3, observations.Count);
        Assert.True(
            observations[0] > compose && observations[0] < intent,
            "f MatchesCapturedPriorState must sit between the COMPOSITION and its PERSISTENCE");
        Assert.True(
            observations[1] > grant && observations[1] < mutated,
            "i MatchesRecordedGrant must sit between h apply grant and j CredentialMutated");
        Assert.True(observations[2] > complete, "m final observation must follow l Done+Active");

        // POSITIVE CONTROL. The SAME predicates applied to the order this file was
        // first written in - Recipe before the grant, and CredentialMutated before
        // the grant was ever proven - must FAIL, so the passes above are calibrated.
        const string SupersededOrder =
            "BeginPromotionIntent PersistPromotedRecipe ApplyApprovedDescriptor "
            + "RecordCredentialMutated ObserveCredential CompletePromotion";

        int oldGrant = SupersededOrder.IndexOf("ApplyApprovedDescriptor", StringComparison.Ordinal);
        int oldMutated = SupersededOrder.IndexOf("RecordCredentialMutated", StringComparison.Ordinal);
        int oldRecipe = SupersededOrder.IndexOf("PersistPromotedRecipe", StringComparison.Ordinal);
        int oldObservation = SupersededOrder.IndexOf("ObserveCredential", StringComparison.Ordinal);

        Assert.False(oldMutated < oldRecipe, "the superseded order put the Recipe FIRST");
        Assert.False(
            oldObservation > oldGrant && oldObservation < oldMutated,
            "the superseded order recorded the mutation BEFORE proving the grant");

        // SECOND POSITIVE CONTROL, for the cycle-90 amendment specifically: the
        // CYCLE-88 order - observe first, then compose and persist in one step -
        // must FAIL the compose-before-observe predicate.
        const string Cycle88Order =
            "CapturePriorDescriptor ObserveCredential TryComposeBeginPromotionIntent "
            + "TryPersistComposedTransition ApplyApprovedDescriptor";

        int oldCompose = Cycle88Order.IndexOf("TryComposeBeginPromotionIntent", StringComparison.Ordinal);
        int oldFirstObservation = Cycle88Order.IndexOf("ObserveCredential", StringComparison.Ordinal);

        Assert.False(
            oldCompose < oldFirstObservation,
            "the cycle-88 order observed the credential BEFORE the intent was composed");
    }

    [Fact]
    public void The_executor_carries_exactly_one_qualified_resolver_invocation()
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(ExecutorPath()));

        Assert.Equal(1, ServiceSidResolverStructuralTests.ResolverInvocationSites(code));

        // ...and the invocation lives in the fixed production port, not in the
        // transaction body, so the transaction itself stays injectable.
        Assert.Contains(
            "class ServiceOwnershipFixedServiceSidPort", code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_executor_names_no_reader_type_anywhere_including_comments()
    {
        string raw = File.ReadAllText(ExecutorPath());

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

        Assert.Contains("class ServiceOwnershipPromotionExecutor", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void No_adapter_accepts_an_arbitrary_path_command_callback_or_strategy()
    {
        Type[] ports = typeof(ServiceOwnershipPromotionExecutor).Assembly
            .GetTypes()
            .Where(t => t.IsInterface && t.Namespace == "PAXCookbookSetup.Service")
            .Where(t => t.Name.StartsWith("IServiceOwnership", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(9, ports.Length);

        foreach (Type port in ports)
        {
            foreach (MethodInfo method in port.GetMethods())
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Assert.False(
                        typeof(Delegate).IsAssignableFrom(parameter.ParameterType),
                        port.Name + "." + method.Name + " accepts a delegate");

                    // The ONLY strings any port accepts are the two bounded
                    // identifiers the ledger itself records.
                    if (parameter.ParameterType == typeof(string))
                    {
                        Assert.Contains(
                            parameter.Name,
                            new[] { "normalizedThumbprintSha1", "promotedJobId" },
                            StringComparer.Ordinal);
                    }
                }
            }
        }
    }

    [Fact]
    public void The_port_names_carry_no_guarded_ledger_token()
    {
        string[] guardedTokens =
        {
            "OwnershipLedgerReader", "OwnershipLedgerWriter", "OwnershipLedgerExecutor",
            "OwnershipLedgerObserver", "ServiceOwnershipLedgerStore", "ServiceOwnershipLedgerRepository",
        };

        Type[] declared = typeof(ServiceOwnershipPromotionExecutor).Assembly
            .GetTypes()
            .Where(t => t.Namespace == "PAXCookbookSetup.Service")
            .Where(t => t.Name.Contains("Promotion", StringComparison.Ordinal)
                        || t.Name.StartsWith("IServiceOwnership", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(declared);

        foreach (Type type in declared)
        {
            foreach (string token in guardedTokens)
            {
                Assert.DoesNotContain(token, type.Name, StringComparison.Ordinal);
            }
        }

        // POSITIVE CONTROL: the same check DOES fire on a name that would trip it.
        Assert.Contains("OwnershipLedgerWriter", "ServiceOwnershipLedgerWriter", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("X509")]
    [InlineData("CngKey")]
    [InlineData("AccessControl")]
    [InlineData("FileSystemAccessRule")]
    [InlineData("RegistryKey")]
    [InlineData("Process.Start")]
    [InlineData("ProcessStartInfo")]
    [InlineData("HttpClient")]
    [InlineData("Socket")]
    [InlineData("OpenSCManager")]
    [InlineData("CreateService")]
    [InlineData("File.")]
    [InlineData("Directory.")]
    [InlineData("FileStream")]
    [InlineData("Environment.")]
    [InlineData("PAX_Purview")]
    [InlineData("StartBake")]
    public void The_executor_contains_no_forbidden_capability(string token)
    {
        string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(ExecutorPath()));

        Assert.False(string.IsNullOrWhiteSpace(code));
        Assert.DoesNotContain(token, code, StringComparison.Ordinal);

        Assert.Contains(
            token,
            SetupCSharpLexicalScanner.ExtractCode("class X { void M() { var y = " + token + " ; } }"),
            StringComparison.Ordinal);
    }

    // CYCLE 94 AMENDMENT (G4), UNDER EXPLICIT AUTHORITY, AND NARROW.
    //
    // WHAT CHANGED. The authorized set grew from ONE file to exactly TWO: the
    // executor file itself and the ONE authorized composition root at
    // src/PAXCookbookSetup/Service/ServiceOwnershipElevatedTransaction.cs.
    //
    // WHAT DID NOT CHANGE. The comparison is still WHOLE CANONICAL PATH equality,
    // OrdinalIgnoreCase, the scan is still comment-and-string stripped, and the
    // calibration control is retained. Every other production file must still
    // contain ZERO constructions and ZERO static or member accesses.
    //
    // WHAT WAS ADDED, BECAUSE THE ALLOWANCE WOULD OTHERWISE BE A BLANK CHEQUE.
    // The root must construct the executor EXACTLY ONCE and invoke Promote and
    // ExecutePlan EXACTLY ONCE EACH, DIRECTLY - and it must not RETAIN the
    // executor in a field or property, because a retained executor is a second
    // transaction waiting to happen.

    private static readonly Func<string, string, bool> ExecutorGuardExactFullPathMatch =
        static (actualFullPath, allowedFullPath) =>
            string.Equals(actualFullPath, allowedFullPath, StringComparison.OrdinalIgnoreCase);

    private static string CompositionRootFullPath() => Path.GetFullPath(Path.Combine(
        RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipElevatedTransaction.cs"));

    private static string[] AuthorizedExecutorFiles() =>
        new[] { Path.GetFullPath(ExecutorPath()), CompositionRootFullPath() };

    /// <summary>
    /// The ONE rule body, shared by the sweep and every control.
    /// </summary>
    private static bool IsExecutorOffender(
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

        return code.Contains("new ServiceOwnershipPromotionExecutor", StringComparison.Ordinal)
            || code.Contains("ServiceOwnershipPromotionExecutor.", StringComparison.Ordinal);
    }

    private static bool IsExecutorOffender(string fileFullPath, string code) =>
        IsExecutorOffender(fileFullPath, code, AuthorizedExecutorFiles(), ExecutorGuardExactFullPathMatch);

    [Fact]
    public void The_executor_allowance_is_exactly_two_distinct_canonical_files()
    {
        string[] allowed = AuthorizedExecutorFiles();

        Assert.Equal(2, allowed.Length);
        Assert.NotEqual(allowed[0], allowed[1], StringComparer.OrdinalIgnoreCase);
        foreach (string path in allowed)
        {
            Assert.Equal(path, Path.GetFullPath(path), StringComparer.Ordinal);
            Assert.True(File.Exists(path), "an authorized executor file is missing: " + path);
        }
    }

    [Fact]
    public void No_production_source_constructs_the_executor()
    {
        string[] sources = Directory
            .GetFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var offenders = new List<string>();
        var referencingFiles = new List<string>();
        int calibration = 0;

        foreach (string file in sources)
        {
            string code = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(file));
            if (code.Contains("ServiceOwnershipLedgerContract", StringComparison.Ordinal))
            {
                calibration++;
            }
            if (IsExecutorOffender(file, code))
            {
                offenders.Add(Path.GetFileName(file));
            }
            if (code.Contains("ServiceOwnershipPromotionExecutor", StringComparison.Ordinal))
            {
                referencingFiles.Add(Path.GetFullPath(file));
            }
        }

        Assert.True(calibration > 0, "the positive control failed, so the zero below is not calibrated");
        Assert.Empty(offenders);

        // EXACT PRESENCE. Exactly the two authorized files may name the executor.
        Assert.Equal(
            AuthorizedExecutorFiles().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            referencingFiles.Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_composition_root_constructs_the_executor_once_and_invokes_both_verbs_directly_once()
    {
        string root = SetupCSharpLexicalScanner.ExtractCode(File.ReadAllText(CompositionRootFullPath()));

        Assert.Equal(1, CountExecutorGuardOccurrences(root, "new ServiceOwnershipPromotionExecutor("));

        // The two verbs are invoked DIRECTLY, by name, exactly once each.
        Assert.Equal(1, CountExecutorGuardOccurrences(root, ".Promote("));
        Assert.Equal(1, CountExecutorGuardOccurrences(root, ".ExecutePlan("));

        // NO STATIC OR MEMBER ACCESS ON THE TYPE, and NO RETAINED EXECUTOR.
        //
        // WHY THIS IS SPELLED AS EXACT SHAPES RATHER THAN A SUBSTRING COUNT.
        // A bare count of "ServiceOwnershipPromotionExecutor" would also count
        // ServiceOwnershipPromotionExecutorOutcome, which is a different, bounded
        // RESULT vocabulary carrying no capability at all - and which the root
        // must name to tell a proven rollback apart from a state that needs a
        // human. So the rule names the shapes that actually matter: the type
        // followed by "(" is a CONSTRUCTION and there is exactly one; the type
        // followed by "." is a STATIC OR MEMBER ACCESS and there are none; and a
        // FIELD or PROPERTY of the type - a RETAINED executor - is matched by its
        // own declaration shape, which a method PARAMETER cannot satisfy.
        Assert.Equal(1, CountExecutorGuardOccurrences(root, "ServiceOwnershipPromotionExecutor("));
        Assert.Equal(0, CountExecutorGuardOccurrences(root, "ServiceOwnershipPromotionExecutor."));

        var retainedExecutor = new Regex(
            @"ServiceOwnershipPromotionExecutor\??\s+[A-Za-z_]\w*\s*(?:;|=[^=>])",
            RegexOptions.CultureInvariant);

        Assert.Equal(0, retainedExecutor.Matches(root).Count);

        // POSITIVE CONTROLS: each shape IS findable when present, a method
        // parameter is NOT mistaken for a retained field, and the outcome
        // vocabulary is not mistaken for any of them.
        Assert.Equal(
            1,
            retainedExecutor.Matches("class X { private readonly ServiceOwnershipPromotionExecutor executor; }").Count);
        Assert.Equal(
            1,
            retainedExecutor.Matches("class X { ServiceOwnershipPromotionExecutor e = Make(); }").Count);
        Assert.Equal(
            0,
            retainedExecutor.Matches("class X { void M(ServiceOwnershipPromotionExecutor executor, int i) { } }").Count);

        const string staticAccess = "class X { void M() { ServiceOwnershipPromotionExecutor.Do(); } }";
        Assert.Equal(1, CountExecutorGuardOccurrences(staticAccess, "ServiceOwnershipPromotionExecutor."));

        const string outcomeOnly = "class X { void M() { var o = ServiceOwnershipPromotionExecutorOutcome.Promoted; } }";
        Assert.Equal(0, CountExecutorGuardOccurrences(outcomeOnly, "ServiceOwnershipPromotionExecutor."));
        Assert.Equal(0, CountExecutorGuardOccurrences(outcomeOnly, "ServiceOwnershipPromotionExecutor("));
        Assert.Equal(0, retainedExecutor.Matches(outcomeOnly).Count);
    }

    [Fact]
    public void A_third_production_file_constructing_the_executor_is_still_an_offender()
    {
        string sibling = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipSomethingElse.cs"));
        const string code = "class X { void M() { var e = new ServiceOwnershipPromotionExecutor(); } }";

        Assert.True(IsExecutorOffender(sibling, code));
        Assert.False(IsExecutorOffender(Path.GetFullPath(ExecutorPath()), code));
        Assert.False(IsExecutorOffender(CompositionRootFullPath(), code));
    }

    [Fact]
    public void MUTATION_matching_the_composition_root_by_leaf_name_is_caught()
    {
        string relocated = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "Elsewhere", "ServiceOwnershipElevatedTransaction.cs"));
        const string code = "class X { void M() { var e = new ServiceOwnershipPromotionExecutor(); } }";

        Assert.True(IsExecutorOffender(relocated, code));

        Func<string, string, bool> leafNameMatch = static (actual, allowed) =>
            string.Equals(Path.GetFileName(actual), Path.GetFileName(allowed), StringComparison.OrdinalIgnoreCase);

        Assert.False(IsExecutorOffender(relocated, code, AuthorizedExecutorFiles(), leafNameMatch));
    }

    [Fact]
    public void MUTATION_permitting_a_third_executor_file_is_caught()
    {
        string probe = Path.GetFullPath(Path.Combine(
            RepoRoot(), "src", "PAXCookbookSetup", "Service", "ServiceOwnershipElevatedTransaction2.cs"));
        const string code = "class X { void M() { ServiceOwnershipPromotionExecutor.Do(); } }";

        Assert.True(IsExecutorOffender(probe, code));

        string[] widened = AuthorizedExecutorFiles().Append(probe).ToArray();
        Assert.Equal(3, widened.Length);
        Assert.False(IsExecutorOffender(probe, code, widened, ExecutorGuardExactFullPathMatch));
    }

    private static int CountExecutorGuardOccurrences(string haystack, string needle)
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
    public void The_results_carry_only_bounded_values()
    {
        foreach (PropertyInfo property in typeof(ServiceOwnershipPromotionResult)
                     .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Assert.True(
                property.PropertyType == typeof(ServiceOwnershipPromotionExecutorOutcome)
                || property.PropertyType == typeof(bool)
                || property.PropertyType == typeof(int),
                property.Name + " is not a bounded value");
        }

        Assert.Equal(
            nameof(ServiceOwnershipPromotionExecutorOutcome.RecoveryRequired),
            ServiceOwnershipPromotionResult.Refused(
                ServiceOwnershipPromotionExecutorOutcome.RecoveryRequired, 0).ToString());

        // A plain refusal reports the SAME value in both slots, so a caller never
        // has to guess whether an unwind happened.
        ServiceOwnershipPromotionResult refused = ServiceOwnershipPromotionResult.Refused(
            ServiceOwnershipPromotionExecutorOutcome.ServiceSidUnavailable, 2);
        Assert.Equal(refused.Outcome, refused.TriggeringFailure);
    }

    [Fact]
    public void Zero_is_never_a_successful_outcome()
    {
        Assert.Equal(0, (int)ServiceOwnershipPromotionExecutorOutcome.Unspecified);
        Assert.Equal(0, (int)ServiceOwnershipDepromotionExecutorOutcome.Unspecified);
        Assert.NotEqual(0, (int)ServiceOwnershipPromotionExecutorOutcome.Promoted);
        Assert.NotEqual(0, (int)ServiceOwnershipDepromotionExecutorOutcome.Depromoted);
        Assert.False(ServiceOwnershipPromotionResult.Refused(default, 0).IsPromoted);
    }
}
