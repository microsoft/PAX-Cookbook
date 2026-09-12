// PAX Cookbook - THE ONE AUTHORIZED OWNERSHIP COMPOSITION ROOT (cycle 94, pass C)
//
// WHAT THIS FILE IS. The single production place where the previously inert
// ownership-promotion surface is assembled and driven. Every guarded capability
// this feature has - the fixed ledger read, the fixed promoted-Recipe store and
// its fixed port, the promotion executor and its two verbs, and every fixed
// native adapter - is named HERE, DIRECTLY, and nowhere else in the product.
//
// WHY "DIRECTLY" IS THE WHOLE POINT. A wrapper, an alias, a reflective lookup, a
// dynamic invocation, a delegate, a factory hidden in an already-exempt file, a
// partial type, a generated source or a token-clean surrogate would each satisfy
// a naive "exactly one call site" count while moving the real capability
// somewhere no guard is looking. So this file contains no reflection, no
// dynamic dispatch, no delegate, no factory and no partial declaration, and a
// structural test proves both halves: the operations are here, and they are
// NOWHERE ELSE.
//
// THE VERB SELECTS THE OPERATION, AND IT DOES SO FIRST. The elevated operation
// is a CLOSED enum fixed by the constructor, which runs before a single payload
// byte exists. Exactly one parser is called, chosen by that fixed operation. The
// document's shape, property set and content NEVER decide what happens.
//
// THE ORDERED PHASES, AND WHAT EACH ONE MAY DO:
//
//   AcceptPayload  runs AT MOST ONCE; calls exactly ONE parser; requires the
//                  request's installation ownership id to equal the id the
//                  channel already validated from the anchor; caches one
//                  accepted typed request; MUTATES NOTHING.
//   Preflight      requires an accepted payload; calls the fixed reader EXACTLY
//                  ONCE; caches the one accepted ledger; derives the
//                  operation-specific entry and job state; constructs NO native
//                  adapter; MUTATES NOTHING.
//   Apply          requires a successful acceptance AND a successful preflight;
//                  requires OWNER CONTINUITY (cycle 96) - a matching, pre-existing
//                  anchor this attempt did not create, whose installation id is
//                  the one AcceptPayload cached and whose initiating user is a
//                  canonical user-shaped SID - BEFORE it constructs anything;
//                  constructs every fixed adapter and the executor exactly once,
//                  binding the owner port to THAT anchor; performs exactly ONE
//                  bounded operation; retains nothing globally or across
//                  transactions.
//
// WHERE THE MACHINE RECIPE STORE LIVES, AND WHO DECIDES. The destination is
// derived HERE, from the fixed machine-data folder plus the fixed names the
// machine-storage contract already owns. No caller path, argument vector,
// environment variable, configuration value, registry value, callback, factory
// or override can influence it, and there is no seam through which one could.
// The store itself may not name the machine-data folder - a certified whole-file
// guard forbids it - so this is the only lawful place for that derivation, and
// it mirrors exactly how the fixed durable ledger write derives its own root.
//
// WHAT THIS FILE CANNOT DO, by construction. It opens no certificate store, no
// private key, no credential vault and no registry key. It starts no process,
// controls no service, opens no socket, runs no PAX and starts no Bake. Every
// effect is reached through a purpose-specific port that names ONE bounded
// operation and accepts no path, SID, descriptor, command or strategy.
//
// PRIVACY. No path, byte, SID, thumbprint, identifier, exception text or native
// status escapes through any result or ToString() - only bounded outcome tokens.
using System;
using System.Globalization;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Service;

/// <summary>
/// The bounded observable outcome of ONE elevated ownership transaction. Zero is
/// the permanent, safe default, so an uninitialised value never reads as success.
/// </summary>
internal enum ServiceOwnershipElevatedTransactionOutcome
{
    Unspecified = 0,

    /// <summary>The one bounded operation completed and verified.</summary>
    Completed = 1,

    /// <summary>The payload was absent, malformed, or refused by the closed contract.</summary>
    PayloadRefused = 2,

    /// <summary>The request named a DIFFERENT installation than the validated anchor.</summary>
    InstallationOwnershipMismatch = 3,

    /// <summary>A mutation-free preflight check refused. Nothing was written.</summary>
    PreflightRefused = 4,

    /// <summary>The fixed ledger could not be read, or was not in an acceptable state.</summary>
    LedgerUnavailable = 5,

    /// <summary>Zero, or more than one, ledger entry carried the named promoted job.</summary>
    EntryNotUniquelyResolved = 6,

    /// <summary>The lifecycle planner refused, or had nothing to do.</summary>
    PlanRefused = 7,

    /// <summary>The bounded operation ran and did not reach its verified end state.</summary>
    ApplyRefused = 8,

    /// <summary>
    /// The machine cannot be proven closed. Never repaired automatically; an
    /// attended, elevated recovery is the only remedy.
    /// </summary>
    RecoveryRequired = 9,
}

/// <summary>
/// THE ONE AUTHORIZED COMPOSITION ROOT. It is the only production implementation
/// of the payload-bearing identity-bound transaction seam, and the only
/// production place any guarded ownership capability is named.
/// </summary>
internal sealed class ServiceOwnershipElevatedTransaction : IServiceIdentityBoundPayloadTransaction
{
    /// <summary>The exact spelling the ledger emits, so a transition round-trips byte for byte.</summary>
    private const string CanonicalUtcTimestampFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'";

    /// <summary>The fixed prefix of a derived entry id. It adds no payload field.</summary>
    private const string DerivedEntryIdPrefix = "entry-";

    private readonly ServiceOwnershipElevatedOperation operation;

    private bool payloadSeen;
    private bool payloadAccepted;
    private bool preflightSeen;
    private bool preflightPassed;
    private bool applySeen;

    private ServicePromotionRequest? promotion;
    private ServiceDepromotionRequest? depromotion;

    private ServiceOwnershipLedgerValidationResult? ledger;
    private ServiceOwnershipLedgerEntry? targetEntry;
    private ServiceOwnershipPromotionKeyHandle targetKey;
    private ServiceOwnershipCapturedPriorDescriptor targetCaptured;
    private ServiceOwnershipJobRelation targetJobRelation;
    private string derivedEntryId = string.Empty;
    private string expectedInstallationOwnershipId = string.Empty;

    /// <summary>
    /// The operation is fixed HERE, at construction, from the verb alone. There is
    /// no setter, no re-bind and no way for a payload to change it.
    /// </summary>
    internal ServiceOwnershipElevatedTransaction(ServiceOwnershipElevatedOperation operation)
    {
        this.operation = operation;
    }

    /// <summary>The bounded observable outcome. Never a path, identifier or exception.</summary>
    internal ServiceOwnershipElevatedTransactionOutcome Outcome { get; private set; }

    /// <summary>How many times the fixed ledger reader was invoked. Success requires exactly one.</summary>
    internal int LedgerReadCount { get; private set; }

    /// <summary>Carries the bounded outcome only.</summary>
    public override string ToString() => Outcome.ToString();

    // -------------------------------------------------------------------
    // PHASE 1 - ACCEPT PAYLOAD. Mutation-free, at most once.
    // -------------------------------------------------------------------

    public ServiceIdentityPayloadAcceptanceState AcceptPayload(string payloadText, string anchorInstallationId)
    {
        // AT MOST ONCE, BY CONSTRUCTION. The flag is set before anything is
        // parsed, so a second entry can never reach a parser.
        if (payloadSeen)
        {
            return Refuse(ServiceOwnershipElevatedTransactionOutcome.PayloadRefused);
        }
        payloadSeen = true;

        if (!ServiceOwnershipLedgerContract.IsValidBoundedToken(
                anchorInstallationId, ServiceOwnershipLedgerContract.MaxStringLength))
        {
            return Refuse(ServiceOwnershipElevatedTransactionOutcome.PayloadRefused);
        }

        // EXACTLY ONE PARSER, CHOSEN BY THE CONSTRUCTOR-FIXED OPERATION. The
        // payload is never inspected to decide which one.
        if (operation == ServiceOwnershipElevatedOperation.Promote)
        {
            ServicePromotionRequestParseResult parsed =
                ServicePromotionRequestParser.ParsePromotion(payloadText);

            if (!parsed.IsAccepted || parsed.Promotion is null)
            {
                return Refuse(ServiceOwnershipElevatedTransactionOutcome.PayloadRefused);
            }

            if (!string.Equals(
                    parsed.Promotion.ExpectedInstallationOwnershipId,
                    anchorInstallationId,
                    StringComparison.Ordinal))
            {
                Outcome = ServiceOwnershipElevatedTransactionOutcome.InstallationOwnershipMismatch;
                return ServiceIdentityPayloadAcceptanceState.InstallationOwnershipMismatch;
            }

            promotion = parsed.Promotion;
        }
        else if (operation == ServiceOwnershipElevatedOperation.Depromote)
        {
            ServicePromotionRequestParseResult parsed =
                ServicePromotionRequestParser.ParseDepromotion(payloadText);

            if (!parsed.IsAccepted || parsed.Depromotion is null)
            {
                return Refuse(ServiceOwnershipElevatedTransactionOutcome.PayloadRefused);
            }

            if (!string.Equals(
                    parsed.Depromotion.ExpectedInstallationOwnershipId,
                    anchorInstallationId,
                    StringComparison.Ordinal))
            {
                Outcome = ServiceOwnershipElevatedTransactionOutcome.InstallationOwnershipMismatch;
                return ServiceIdentityPayloadAcceptanceState.InstallationOwnershipMismatch;
            }

            depromotion = parsed.Depromotion;
        }
        else
        {
            return Refuse(ServiceOwnershipElevatedTransactionOutcome.PayloadRefused);
        }

        // THE EXPECTED OWNERSHIP ID IS BOUND TO THE VALIDATED ANCHOR, not to the
        // wire: the two were just proven equal, and this is the copy every later
        // phase uses.
        expectedInstallationOwnershipId = anchorInstallationId;
        payloadAccepted = true;
        return ServiceIdentityPayloadAcceptanceState.Accepted;
    }

    // -------------------------------------------------------------------
    // PHASE 2 - PREFLIGHT. Mutation-free. Exactly one ledger read.
    // -------------------------------------------------------------------

    public ServiceIdentityBoundTransactionPreflightState Preflight()
    {
        if (preflightSeen || !payloadAccepted)
        {
            return RefusePreflight(ServiceOwnershipElevatedTransactionOutcome.PreflightRefused);
        }
        preflightSeen = true;

        // THE ONE AND ONLY LEDGER READ IN THE WHOLE PRODUCT.
        LedgerReadCount++;
        ServiceOwnershipLedgerReadResult read = ServiceOwnershipLedgerReader.Read();

        // The reader's own verdict is consumed as-is. Nothing here re-derives it.
        if (read.Validation is null || !read.Validation.IsAccepted)
        {
            return RefusePreflight(ServiceOwnershipElevatedTransactionOutcome.LedgerUnavailable);
        }

        ledger = read.Validation;

        return operation == ServiceOwnershipElevatedOperation.Promote
            ? PreflightPromotion()
            : PreflightDepromotion();
    }

    private ServiceIdentityBoundTransactionPreflightState PreflightPromotion()
    {
        // A PROMOTION MAY ONLY BEGIN FROM NOTHING. An in-progress, stale or active
        // ledger is not a starting state, and there is no force option anywhere.
        if (ledger!.Outcome is not (ServiceOwnershipLedgerOutcome.Absent
            or ServiceOwnershipLedgerOutcome.ValidEmpty))
        {
            return RefusePreflight(ServiceOwnershipElevatedTransactionOutcome.LedgerUnavailable);
        }

        // THE ENTRY ID IS DERIVED, NOT SUPPLIED. It adds no payload field: it is
        // the fixed prefix plus the promoted job id the request already carries,
        // and it must satisfy the ledger's own bounded-token grammar.
        string candidate = DerivedEntryIdPrefix + promotion!.PromotedJobId;
        if (!ServiceOwnershipLedgerContract.IsValidBoundedToken(
                candidate, ServiceOwnershipLedgerContract.MaxStringLength))
        {
            return RefusePreflight(ServiceOwnershipElevatedTransactionOutcome.PreflightRefused);
        }

        derivedEntryId = candidate;
        preflightPassed = true;
        return ServiceIdentityBoundTransactionPreflightState.Proceed;
    }

    private ServiceIdentityBoundTransactionPreflightState PreflightDepromotion()
    {
        ServiceOwnershipLedgerDocument? document = ledger!.Document;
        if (document is null)
        {
            return RefusePreflight(ServiceOwnershipElevatedTransactionOutcome.LedgerUnavailable);
        }

        // The ledger must be THIS installation's. The id was already bound to the
        // validated anchor, so this compares two facts neither of which came from
        // the payload.
        if (!string.Equals(
                document.InstallationOwnershipId,
                expectedInstallationOwnershipId,
                StringComparison.Ordinal))
        {
            return RefusePreflight(ServiceOwnershipElevatedTransactionOutcome.LedgerUnavailable);
        }

        // EXACTLY ONE ENTRY MAY CARRY THE NAMED JOB. Zero is refused and so is
        // more than one: an ambiguous job is never resolved by preference.
        string jobId = depromotion!.PromotedJobId;
        ServiceOwnershipLedgerEntry? found = null;
        int matches = 0;
        foreach (ServiceOwnershipLedgerEntry entry in document.Entries)
        {
            if (CarriesJob(entry, jobId))
            {
                matches++;
                found = entry;
            }
        }

        if (matches != 1 || found is null)
        {
            return RefusePreflight(ServiceOwnershipElevatedTransactionOutcome.EntryNotUniquelyResolved);
        }

        // EVERY DERIVED FACT COMES FROM THE ACCEPTED CACHED LEDGER AND NOTHING
        // ELSE. No certificate store, key, descriptor or identity source is read
        // here, and no native adapter exists yet.
        targetEntry = found;
        derivedEntryId = found.EntryId;
        targetKey = ServiceOwnershipPromotionKeyHandle.Create(
            found.KeyIdentity,
            found.ProviderUniqueName,
            found.PrivateKeyProviderKind,
            found.KeyStorageRoot,
            found.DescriptorFormat);
        targetCaptured = ServiceOwnershipCapturedPriorDescriptor.Captured(
            found.PriorDaclState, found.PriorDaclBytesBase64, found.PriorDaclSha256);
        targetJobRelation = found.AssociatedPromotedJobIds.Count > 1
            ? ServiceOwnershipJobRelation.OtherJobsRemain
            : ServiceOwnershipJobRelation.FinalAssociatedJob;

        if (!targetKey.IsUsable || !targetCaptured.IsCaptured)
        {
            return RefusePreflight(ServiceOwnershipElevatedTransactionOutcome.PreflightRefused);
        }

        preflightPassed = true;
        return ServiceIdentityBoundTransactionPreflightState.Proceed;
    }

    private static bool CarriesJob(ServiceOwnershipLedgerEntry entry, string jobId)
    {
        foreach (string associated in entry.AssociatedPromotedJobIds)
        {
            if (string.Equals(associated, jobId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    // -------------------------------------------------------------------
    // PHASE 3 - APPLY. Exactly one bounded operation.
    // -------------------------------------------------------------------

    public ServiceIdentityBoundTransactionResult Apply(ServiceIdentityBoundTransactionContext context)
    {
        if (applySeen || !payloadAccepted || !preflightPassed || ledger is null)
        {
            return RefuseApply(ServiceOwnershipElevatedTransactionOutcome.ApplyRefused);
        }
        applySeen = true;

        // CYCLE 96 - OWNER CONTINUITY, REQUIRED BEFORE ANYTHING IS CONSTRUCTED.
        //
        // The channel builds this context's anchor from the KERNEL-DERIVED SID and
        // the VALIDATED installation id, strictly before any payload byte is
        // parsed, and it refuses outright when a durable anchor names a different
        // initiator. So the anchor carried here IS the authenticated identity.
        // Before cycle 96 this method threw that away and let the owner port
        // re-read the anchor unbound; now the four facts below are required first,
        // and the port is bound to this exact document.
        //
        // NONE of these four can be influenced by the payload: the presence and
        // creation facts are the channel's, the anchor is the channel's, and the
        // installation id is the copy AcceptPayload cached from the already
        // validated anchor.
        if (context.Presence != ServiceAnchorPresence.MatchingPresent
            || context.AnchorCreatedThisAttempt
            || context.Anchor is null)
        {
            return RefuseApply(ServiceOwnershipElevatedTransactionOutcome.ApplyRefused);
        }

        // THE ANCHOR MUST BE THIS TRANSACTION'S INSTALLATION. Its own bounded
        // mismatch token is used, because that is exactly what this is.
        if (!string.Equals(
                context.Anchor.InstallationId,
                expectedInstallationOwnershipId,
                StringComparison.Ordinal))
        {
            Outcome = ServiceOwnershipElevatedTransactionOutcome.InstallationOwnershipMismatch;
            return ServiceIdentityBoundTransactionResult.Compensated(anchorRemoved: false);
        }

        // AND ITS OWNER MUST BE A CANONICAL, USER-SHAPED SID. The same certified
        // pair the ownership ledger's own transition facts apply, so the two ends
        // can never disagree about what a user owner is.
        if (!ServiceOwnershipLedgerContract.IsCanonicalSidString(context.Anchor.InitiatingUserSid)
            || !ServiceOwnershipLedgerContract.IsUserOwnerSid(context.Anchor.InitiatingUserSid))
        {
            return RefuseApply(ServiceOwnershipElevatedTransactionOutcome.ApplyRefused);
        }

        // THE ONE PLACE EVERY FIXED ADAPTER IS CONSTRUCTED, EACH EXACTLY ONCE.
        // Each is an instance with no static entry point, so an adapter that is
        // never constructed can never run - and none of them is constructed
        // anywhere else in the product.
        //
        // THE STORE ROOT IS DERIVED HERE AND ONLY HERE. The fixed machine data
        // folder is the ONLY input; every folder beneath it is the store's own
        // fixed contract name, which no caller can reach.
        string machineDataRoot;
        try
        {
            machineDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        }
        catch (Exception)
        {
            return RefuseApply(ServiceOwnershipElevatedTransactionOutcome.ApplyRefused);
        }

        if (string.IsNullOrWhiteSpace(machineDataRoot))
        {
            return RefuseApply(ServiceOwnershipElevatedTransactionOutcome.ApplyRefused);
        }

        using var promotedRecipePort =
            new ServiceOwnershipFixedPromotedRecipePort(new ServiceOwnershipPromotedRecipeStore(machineDataRoot));

        // ONE observer instance, used by the executor's own precondition and grant
        // checks AND by the depromotion path below. A second instance would be a
        // second, unaudited credential-observation site.
        var credentialObserver = new ServiceOwnershipCertifiedEntryObservationAdapter();

        var executor = new ServiceOwnershipPromotionExecutor(
            new ServiceOwnershipFixedOwnerIdentityPort(context.Anchor),
            new ServiceOwnershipFixedServiceSidPort(),
            new ServiceOwnershipFixedCertificateFactsPort(),
            new ServiceOwnershipFixedPriorDescriptorPort(),
            new ServiceOwnershipFixedApprovedDescriptorPort(),
            new ServiceOwnershipFixedDescriptorRestorePort(),
            credentialObserver,
            new ServiceOwnershipFixedLedgerPersistencePort(),
            promotedRecipePort);

        string stamp = DateTime.UtcNow.ToString(CanonicalUtcTimestampFormat, CultureInfo.InvariantCulture);

        return operation == ServiceOwnershipElevatedOperation.Promote
            ? ApplyPromotion(executor, stamp)
            : ApplyDepromotion(executor, credentialObserver, stamp);
    }

    private ServiceIdentityBoundTransactionResult ApplyPromotion(
        ServiceOwnershipPromotionExecutor executor, string stamp)
    {
        // EXACTLY ONE PROMOTION, FROM THE CACHED REQUEST AND THE CACHED LEDGER.
        // Every identity, descriptor and grant fact is observed inside the
        // executor through its purpose-specific ports; nothing is supplied here.
        ServiceOwnershipPromotionResult result =
            executor.Promote(promotion, ledger, derivedEntryId, stamp);

        // ONLY A FULLY PROMOTED TRANSACTION IS A SUCCESS. Everything else is a
        // failure, and the one failure that a human must look at keeps its own
        // terminal state instead of being collapsed into an ordinary refusal.
        if (result.IsPromoted)
        {
            Outcome = ServiceOwnershipElevatedTransactionOutcome.Completed;
            return ServiceIdentityBoundTransactionResult.Completed();
        }

        if (result.Outcome == ServiceOwnershipPromotionExecutorOutcome.RecoveryRequired)
        {
            return RefuseApply(ServiceOwnershipElevatedTransactionOutcome.RecoveryRequired);
        }

        Outcome = ServiceOwnershipElevatedTransactionOutcome.ApplyRefused;
        return ServiceIdentityBoundTransactionResult.Compensated(anchorRemoved: false);
    }

    private ServiceIdentityBoundTransactionResult ApplyDepromotion(
        ServiceOwnershipPromotionExecutor executor,
        ServiceOwnershipCertifiedEntryObservationAdapter credentialObserver,
        string stamp)
    {
        // THE CREDENTIAL IS OBSERVED THROUGH THE CERTIFIED ENTRY OBSERVER, judged
        // against the certified entry the accepted ledger already carries. No
        // free-form fact, path, SID or descriptor byte reaches the observer.
        ServiceOwnershipCredentialObservation observation =
            credentialObserver.ObserveCredential(targetEntry!);

        // EXACTLY ONE PLAN REQUEST, AND IT IS THE JOB-UNPUBLISH ONE. No other
        // lifecycle factory is reachable from this file.
        ServiceOwnershipLifecyclePlanRequest? request = ServiceOwnershipLifecyclePlanRequest.ForUnpublishJob(
            ledger,
            expectedInstallationOwnershipId,
            targetEntry!.OwningUserSid,
            targetEntry!.ServiceSid,
            derivedEntryId,
            depromotion!.PromotedJobId,
            observation,
            targetJobRelation);

        if (request is null)
        {
            return RefuseApply(ServiceOwnershipElevatedTransactionOutcome.PlanRefused);
        }

        // EXACTLY ONE PLAN.
        ServiceOwnershipLifecyclePlan plan = ServiceOwnershipLifecyclePlanner.Plan(request);
        if (plan.Outcome != ServiceOwnershipPlanOutcome.PlanAvailable)
        {
            return RefuseApply(ServiceOwnershipElevatedTransactionOutcome.PlanRefused);
        }

        // EXACTLY ONE EXECUTION OF THAT PLAN. The certificate and its private key
        // are never created, exported or deleted anywhere on this path: the only
        // descriptor the restore port can be handed is the one the ledger itself
        // captured.
        ServiceOwnershipDepromotionResult result = executor.ExecutePlan(
            plan,
            ledger,
            targetKey,
            targetCaptured,
            derivedEntryId,
            depromotion!.PromotedJobId,
            depromotion!.OperationId,
            stamp);

        if (result.IsDepromoted)
        {
            Outcome = ServiceOwnershipElevatedTransactionOutcome.Completed;
            return ServiceIdentityBoundTransactionResult.Completed();
        }

        // AN UNPROVEN RESTORATION IS NEVER A SUCCESS. Both the stale marking and
        // the unproven restore keep their own terminal state, because both mean a
        // human has to finish the job by hand.
        if (result.Outcome is ServiceOwnershipDepromotionExecutorOutcome.MarkedStale
            or ServiceOwnershipDepromotionExecutorOutcome.RestoreNotProven)
        {
            return RefuseApply(ServiceOwnershipElevatedTransactionOutcome.RecoveryRequired);
        }

        Outcome = ServiceOwnershipElevatedTransactionOutcome.ApplyRefused;
        return ServiceIdentityBoundTransactionResult.Compensated(anchorRemoved: false);
    }

    // -------------------------------------------------------------------
    // BOUNDED REFUSALS
    // -------------------------------------------------------------------

    private ServiceIdentityPayloadAcceptanceState Refuse(ServiceOwnershipElevatedTransactionOutcome outcome)
    {
        Outcome = outcome;
        return ServiceIdentityPayloadAcceptanceState.Refused;
    }

    private ServiceIdentityBoundTransactionPreflightState RefusePreflight(
        ServiceOwnershipElevatedTransactionOutcome outcome)
    {
        Outcome = outcome;
        return ServiceIdentityBoundTransactionPreflightState.Refused;
    }

    private ServiceIdentityBoundTransactionResult RefuseApply(
        ServiceOwnershipElevatedTransactionOutcome outcome)
    {
        Outcome = outcome;
        return outcome == ServiceOwnershipElevatedTransactionOutcome.RecoveryRequired
            ? ServiceIdentityBoundTransactionResult.RecoveryRequired()
            : ServiceIdentityBoundTransactionResult.Compensated(anchorRemoved: false);
    }
}
