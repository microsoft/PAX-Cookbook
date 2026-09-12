using PAXCookbook.Shared.Contracts;

namespace PAXCookbook.App;

// Cycle 16 — THE SOLE PRODUCTION WIRING LOCATION for the organization
// authority chain.
//
// Before this cycle the real chain was assembled inline in the Chef's Keys list
// route while the Cook / readiness path injected null inventory, catalog, and
// clock dependencies, so production could never compute the truthful final
// readiness state. Every production consumer now builds the SAME chain here,
// exactly once:
//
//   machine policy -> managed-key authorization gate -> trusted ProgramData
//   inventory -> fixed LocalMachine\My certificate catalog -> system clock
//   (-> acquired-engine capability, for a full snapshot)
//
// Hard properties:
//   * No caller-supplied path, store, policy, catalog, clock, or capability.
//   * No environment-variable, HTTP, or React override of any dependency.
//   * No static / lazy / memoized caching of authority ACROSS calls: every
//     invocation constructs fresh instances, so a readiness call can never hand
//     a stale authorization, inventory, certificate, or capability to a later
//     Cook. Within ONE returned authority the catalog de-duplicates a single
//     store read (OnceQueriedCertificateCatalog); that instance is deliberately
//     NOT hoisted to a field.
//   * Nothing here is disposable: MachineCertificateCatalog opens and disposes
//     its own read-only store per query.
//   * This file constructs no process, runs no PAX, performs no Bake, writes
//     nothing, and reads no private key or secret.
internal static class ProductionOrganizationAuthority
{
    // The LOCAL half of the authority: everything that can be decided without
    // the acquired engine. This is what the read-only Chef's Keys list needs.
    internal static LocalOrganizationAuthority CreateLocalAuthority()
    {
        // 1-2. Machine policy, then the managed Chef's Keys authorization gate.
        MachinePolicyDetection detection =
            MachinePolicyParser.Classify(new MachinePolicyRegistrySource().Read());
        ManagedChefKeysGateProjection gate = ManagedChefKeysGate.Evaluate(detection);

        // 3-5. The gate short-circuits FIRST: the read-only, trust-validating
        //      ProgramData source is consulted only when policy authorizes an
        //      inventory, and the document only parses -- no key is resolved or
        //      activated here.
        OrganizationInventoryEvaluation evaluation =
            OrganizationKeyInventoryEvaluator.EvaluateDetailed(
                gate, new ProgramDataOrganizationKeyInventorySource());

        // 6-8. The catalog is LAZY: it opens the fixed LocalMachine\My store at
        //      most once, and only if an evaluator actually needs a lookup.
        return new LocalOrganizationAuthority(
            evaluation,
            new OnceQueriedCertificateCatalog(new MachineCertificateCatalog()),
            new SystemCertificateUsabilityClock());
    }

    // The FULL snapshot: the local authority plus step 9, the ACTIVE acquired
    // engine's sanctioned-selector capability. The capability read is DEFERRED,
    // so an unauthorized policy or an unready binding never consults the
    // acquired-engine record at all. Because the current production engine
    // declares no capability, a binding that does reach step 9 always yields a
    // non-Available state and organization Bakes stay blocked.
    //
    // `versionInfo` is the caller's already-resolved version context; the
    // capability record is bound to the acquisition's own verified hash and
    // version inside EngineCapabilityRuntime, so nothing is re-derived here.
    internal static RecipeReadModel.OrganizationCookPreparationContext CreateSnapshot(
        VersionInfo versionInfo, EngineAcquisitionResult? engine)
    {
        LocalOrganizationAuthority local = CreateLocalAuthority();
        return new RecipeReadModel.OrganizationCookPreparationContext(
            () => EngineCapabilityRuntime.Evaluate(
                engine, EngineCapabilityRuntime.OrganizationCertificateSha256Selector),
            local.Evaluation,
            local.Catalog,
            local.Clock);
    }
}

// The three LOCAL dependencies, carried together so no consumer can assemble a
// partial chain. It holds no disposable resource and no secret.
internal sealed record LocalOrganizationAuthority(
    OrganizationInventoryEvaluation Evaluation,
    ICertificateCatalog Catalog,
    ICertificateUsabilityClock Clock);
