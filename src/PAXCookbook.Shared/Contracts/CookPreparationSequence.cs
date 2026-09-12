namespace PAXCookbook.Shared.Contracts;

// Single-source, cross-component contract for the ORDER OF THE THREE COOK
// PREPARATION PHASES.
//
// WHAT THIS TYPE OWNS — and nothing more:
//   phase 1  gates          -> phase 2  authorization -> phase 3  preparation
//
// WHAT THIS TYPE DOES NOT OWN. It does NOT own cook gates 1..18; seventeen of the
// eighteen gates keep their ordering inside existing host-owned helpers by
// design. It does not own the child spawn or the supervisor, which sit OUTSIDE
// the sequence entirely. It is NOT the canonical cook pipeline. The Resume
// trigger now runs this phase ORDER, but Resume has no recipe, so its GATES are
// deliberately different gates and are NOT converged with the recipe gates.
//
// WHAT CYCLE 35 CHANGED. The obsolete PrepareCookStart no-child seam has been
// RETIRED, so every SUPPORTED PRODUCTION cook execution entry point now
// traverses this sequence and no unsequenced caller remains. That is a statement
// about ROUTING ONLY. It does NOT make this type canonical — it still owns three
// phase names and nothing else — it does NOT converge the per-trigger gates, and
// it does NOT make the Windows service cook-ready: the service still refuses to
// prepare or run a cook.
//
// THIS FILE IS THE SINGLE SOURCE OF TRUTH for that phase order. It is compiled
// into PAXCookbook.Shared by default globbing AND compile-LINKED into
// PAXCookbook.App and PAXCookbook.Service, neither of which references Shared.
// Three compiled copies of ONE source file, exactly like the seven contracts
// linked alongside it — never three hand-maintained orders that can drift.
//
// Doctrine (binding, and enforced by SHAPE rather than by this comment — every
// member below traffics only in bool and the closed enums declared in this file):
//   - The surface carries no object, dynamic, string, exception text, filesystem
//     path, command, credential, certificate, token, identifier, recipe tree,
//     HTTP body, process handle, database handle, App type, or Service type. A
//     host's own state never crosses this boundary; the host keeps it privately
//     inside its own adapter.
//   - Execute ITSELF invokes the three phases in fixed order. There is no step
//     list for a host to enumerate, loop over, reorder, or skip, and no hook that
//     would let an adapter influence ordering. The SEQUENCE decides WHICH
//     authorization phase applies to the trigger; the adapter supplies BEHAVIOUR
//     only.
//   - Everything FAILS CLOSED. An undeclared trigger value refuses without
//     invoking a single adapter phase. Any phase that does not proceed
//     short-circuits every later phase. The zero value of every result type is a
//     refusal, never a success.
public static class CookPreparationSequence
{
    // Runs the three preparation phases in their fixed order for the supplied
    // trigger. Returns the bounded disposition; the host reads its own bounded
    // refusal (status, body, or equivalent) from its own adapter, because no such
    // value is permitted to cross this contract.
    public static CookPreparationOutcome Execute(
        CookTriggerKind trigger,
        ICookPreparationAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        // FAIL CLOSED on an undeclared trigger BEFORE any adapter phase runs. A
        // value outside the closed set is never treated as manual by default.
        if (!IsDeclaredTrigger(trigger))
        {
            return new CookPreparationOutcome(
                CookPreparationDisposition.RefusedUnsupportedTrigger,
                CookPreparationPhase.None);
        }

        // Phase 1 — gates.
        if (!adapter.EvaluateGatesPhase())
        {
            return new CookPreparationOutcome(
                CookPreparationDisposition.RefusedAtGates,
                CookPreparationPhase.Gates);
        }

        // Phase 2 — authorization. The SEQUENCE selects which authorization phase
        // applies, and invokes the selected one EXACTLY ONCE. That is not a
        // stylistic preference: a scheduled authorization may legitimately CONSUME
        // a one-shot operator marker, so a second invocation would silently
        // consume a second occurrence.
        //
        // This is an EXHAUSTIVE switch, never a binary fallback. A fallback of the
        // form "scheduled ? scheduled : manual" would silently route every trigger
        // that is not Scheduled — including a newly declared one — into MANUAL
        // authorization. The default arm here refuses instead, before preparation.
        // It is unreachable while the guard above and this switch agree on the
        // declared set; it exists so that they cannot silently disagree.
        bool authorized = trigger switch
        {
            CookTriggerKind.Manual => adapter.AuthorizeManualPhase(),
            CookTriggerKind.Scheduled => adapter.AuthorizeScheduledPhase(),
            CookTriggerKind.Resume => adapter.AuthorizeResumePhase(),
            _ => false,
        };
        if (!authorized)
        {
            return new CookPreparationOutcome(
                CookPreparationDisposition.RefusedAtAuthorization,
                CookPreparationPhase.Authorization);
        }

        // Phase 3 — preparation.
        if (!adapter.PreparePhase())
        {
            return new CookPreparationOutcome(
                CookPreparationDisposition.RefusedAtPreparation,
                CookPreparationPhase.Preparation);
        }

        return new CookPreparationOutcome(
            CookPreparationDisposition.Prepared,
            CookPreparationPhase.Preparation);
    }

    // The declared trigger set, as an exhaustive switch rather than a chain of
    // inequalities: adding a kind to the enum without adding it here refuses it,
    // which is the safe direction.
    private static bool IsDeclaredTrigger(CookTriggerKind trigger) => trigger switch
    {
        CookTriggerKind.Manual => true,
        CookTriggerKind.Scheduled => true,
        CookTriggerKind.Resume => true,
        _ => false,
    };
}

// The exact, closed set of cook triggers. There is deliberately NO zero member:
// default(CookTriggerKind) is therefore not a declared kind and fails closed.
public enum CookTriggerKind
{
    Manual = 1,
    Scheduled = 2,
    Resume = 3,
}

// The exact, closed set of preparation phases, in their fixed order. None is the
// zero value so an unset phase can never read as "preparation completed".
public enum CookPreparationPhase
{
    None = 0,
    Gates = 1,
    Authorization = 2,
    Preparation = 3,
}

// The exact, closed set of sequence dispositions. The zero value is a refusal, so
// a default-initialised outcome is never mistaken for a prepared cook.
public enum CookPreparationDisposition
{
    RefusedUnsupportedTrigger = 0,
    Prepared = 1,
    RefusedAtGates = 2,
    RefusedAtAuthorization = 3,
    RefusedAtPreparation = 4,
}

// The bounded result of one sequence run: what happened, and how far it got.
// Carries no status code, no body, no identifier, and no host state.
public readonly record struct CookPreparationOutcome(
    CookPreparationDisposition Disposition,
    CookPreparationPhase FurthestPhaseInvoked)
{
    public bool Prepared => Disposition == CookPreparationDisposition.Prepared;
}

// The behaviour a host supplies for each phase. Each member answers exactly one
// question — "did this phase proceed?" — and nothing else. A host that refuses
// records its own bounded refusal privately, on its own side of this boundary.
//
// The adapter cannot influence phase ORDER: it is never handed a step list, never
// asked what to run next, and never consulted about sequencing.
//
// A host implements ALL THREE authorization phases even though it serves only the
// trigger(s) it is constructed for. An authorization phase for a trigger a host
// can NEVER serve must return FALSE and record a bounded refusal. Returning true
// from an impossible adapter/trigger combination would authorize a mis-routed
// trigger silently, which is exactly the failure the exhaustive switch in Execute
// exists to prevent.
public interface ICookPreparationAdapter
{
    // Phase 1. False refuses the cook before any authorization or preparation.
    bool EvaluateGatesPhase();

    // Phase 2, selected by the sequence for CookTriggerKind.Manual.
    //
    // On the desktop host this is a NO-OP POLICY ACKNOWLEDGEMENT, not an active
    // authorization check: a manual cook is already authorized UPSTREAM by the
    // Unlocked broker session plus the explicit user confirmation that issued the
    // request. Its value here is that the sequence invokes it — and only it — for
    // a manual trigger, which is what proves phase SELECTION and proves that no
    // new identity ceremony was introduced.
    bool AuthorizeManualPhase();

    // Phase 2, selected by the sequence for CookTriggerKind.Scheduled. The
    // sequence invokes this exactly once per run. This is the one authorization
    // phase that performs real work on the desktop host.
    bool AuthorizeScheduledPhase();

    // Phase 2, selected by the sequence for CookTriggerKind.Resume.
    //
    // Like the manual phase this is a NO-OP POLICY ACKNOWLEDGEMENT, not an active
    // authorization check: a Resume is already authorized UPSTREAM by the Unlocked
    // broker session plus the explicit user confirmation. It is a distinct member
    // so that a Resume can never be served by the MANUAL phase, and so a host that
    // cannot serve a Resume can refuse it explicitly.
    bool AuthorizeResumePhase();

    // Phase 3. False refuses the cook before the host's spawn boundary is reached.
    bool PreparePhase();
}
