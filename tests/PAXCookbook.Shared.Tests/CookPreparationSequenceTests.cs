using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PAXCookbook.Shared.Contracts;
using Xunit;

namespace PAXCookbook.Shared.Tests;

// Cycle 33 - PURE ORCHESTRATION coverage for the portable
// CookPreparationSequence. Cycle 34 adds the Resume trigger. Cycle 35 retires the
// obsolete no-child preparation seam.
//
// SCOPE, stated plainly so nobody over-reads these tests:
//   * This type owns the ORDER OF THREE PHASES and nothing else. It does NOT own
//     cook gates 1-18; seventeen of the eighteen gates keep their ordering inside
//     existing App-owned helpers by design. It is NOT canonical: Resume now runs
//     this phase ORDER, but Resume has no recipe, so its GATES are different gates
//     and are not converged.
//   * Cycle 35 RETIRED the obsolete PrepareCookStart no-child seam, so every
//     SUPPORTED PRODUCTION cook execution entry point now traverses the sequence
//     and no unsequenced caller remains. Routing only: the sequence is still NOT
//     canonical, and the Windows service is still NOT cook-ready.
//   * Every test here runs against a recording FAKE adapter. Nothing in this file
//     touches a filesystem, a database, a process, a network endpoint, a
//     credential, the PAX engine, or a Bake.
//
// Every negative assertion below is paired with a POSITIVE CONTROL that proves the
// same assertion is capable of failing.
public class CookPreparationSequenceTests
{
    // A recording adapter. It deliberately exposes a "preferred" order that the
    // sequence must ignore: the adapter supplies BEHAVIOUR only, never order.
    private sealed class RecordingAdapter : ICookPreparationAdapter
    {
        private readonly HashSet<CookPreparationPhase> _refuseAt;

        internal RecordingAdapter(params CookPreparationPhase[] refuseAt)
        {
            _refuseAt = new HashSet<CookPreparationPhase>(refuseAt);
        }

        internal List<string> Calls { get; } = new();

        // Intentionally ignored by the sequence. Present so the "adapter cannot
        // reorder phases" test has something that WOULD reorder if it were honoured.
        internal IReadOnlyList<string> PreferredOrder { get; } = new[]
        {
            nameof(PreparePhase), nameof(AuthorizeScheduledPhase), nameof(EvaluateGatesPhase),
        };

        internal int ScheduledAuthorizationCalls { get; private set; }

        internal int ManualAuthorizationCalls { get; private set; }

        internal int ResumeAuthorizationCalls { get; private set; }

        public bool EvaluateGatesPhase()
        {
            Calls.Add(nameof(EvaluateGatesPhase));
            return !_refuseAt.Contains(CookPreparationPhase.Gates);
        }

        public bool AuthorizeManualPhase()
        {
            Calls.Add(nameof(AuthorizeManualPhase));
            ManualAuthorizationCalls++;
            return !_refuseAt.Contains(CookPreparationPhase.Authorization);
        }

        public bool AuthorizeScheduledPhase()
        {
            Calls.Add(nameof(AuthorizeScheduledPhase));
            ScheduledAuthorizationCalls++;
            return !_refuseAt.Contains(CookPreparationPhase.Authorization);
        }

        public bool AuthorizeResumePhase()
        {
            Calls.Add(nameof(AuthorizeResumePhase));
            ResumeAuthorizationCalls++;
            return !_refuseAt.Contains(CookPreparationPhase.Authorization);
        }

        public bool PreparePhase()
        {
            Calls.Add(nameof(PreparePhase));
            return !_refuseAt.Contains(CookPreparationPhase.Preparation);
        }
    }

    // An adapter whose MANUAL authorization phase is a landmine. If any trigger
    // other than Manual ever fell through to manual authorization, the sequence
    // would throw rather than quietly authorize.
    private sealed class ManualAuthorizationLandmineAdapter : ICookPreparationAdapter
    {
        internal List<string> Calls { get; } = new();

        public bool EvaluateGatesPhase()
        {
            Calls.Add(nameof(EvaluateGatesPhase));
            return true;
        }

        public bool AuthorizeManualPhase()
        {
            Calls.Add(nameof(AuthorizeManualPhase));
            throw new InvalidOperationException("manual authorization must not be reachable for this trigger");
        }

        public bool AuthorizeScheduledPhase()
        {
            Calls.Add(nameof(AuthorizeScheduledPhase));
            return true;
        }

        public bool AuthorizeResumePhase()
        {
            Calls.Add(nameof(AuthorizeResumePhase));
            return true;
        }

        public bool PreparePhase()
        {
            Calls.Add(nameof(PreparePhase));
            return true;
        }
    }

    // The authorization member the sequence is required to select for each
    // declared trigger. Kept as data so the exhaustive test below fails loudly if
    // a trigger is added to the enum without a routing decision.
    private static readonly Dictionary<CookTriggerKind, string> AuthorizationMemberForTrigger = new()
    {
        [CookTriggerKind.Manual] = "AuthorizeManualPhase",
        [CookTriggerKind.Scheduled] = "AuthorizeScheduledPhase",
        [CookTriggerKind.Resume] = "AuthorizeResumePhase",
    };

    // ---- phase order ---------------------------------------------------------

    [Fact]
    public void Manual_runs_gates_then_manual_authorization_then_preparation()
    {
        RecordingAdapter adapter = new();

        CookPreparationOutcome outcome =
            CookPreparationSequence.Execute(CookTriggerKind.Manual, adapter);

        Assert.Equal(
            new[] { "EvaluateGatesPhase", "AuthorizeManualPhase", "PreparePhase" },
            adapter.Calls);
        Assert.Equal(CookPreparationDisposition.Prepared, outcome.Disposition);
        Assert.Equal(CookPreparationPhase.Preparation, outcome.FurthestPhaseInvoked);
        Assert.True(outcome.Prepared);

        // The scheduled and resume authorization phases are NOT part of a manual
        // sequence.
        Assert.Equal(0, adapter.ScheduledAuthorizationCalls);
        Assert.Equal(0, adapter.ResumeAuthorizationCalls);
        Assert.Equal(1, adapter.ManualAuthorizationCalls);
    }

    [Fact]
    public void Scheduled_runs_gates_then_scheduled_authorization_then_preparation()
    {
        RecordingAdapter adapter = new();

        CookPreparationOutcome outcome =
            CookPreparationSequence.Execute(CookTriggerKind.Scheduled, adapter);

        Assert.Equal(
            new[] { "EvaluateGatesPhase", "AuthorizeScheduledPhase", "PreparePhase" },
            adapter.Calls);
        Assert.Equal(CookPreparationDisposition.Prepared, outcome.Disposition);
        Assert.DoesNotContain("AuthorizeManualPhase", adapter.Calls);
    }

    [Fact]
    public void Resume_runs_gates_then_resume_authorization_then_preparation()
    {
        RecordingAdapter adapter = new();

        CookPreparationOutcome outcome =
            CookPreparationSequence.Execute(CookTriggerKind.Resume, adapter);

        Assert.Equal(
            new[] { "EvaluateGatesPhase", "AuthorizeResumePhase", "PreparePhase" },
            adapter.Calls);
        Assert.Equal(CookPreparationDisposition.Prepared, outcome.Disposition);
        Assert.Equal(CookPreparationPhase.Preparation, outcome.FurthestPhaseInvoked);

        // Neither of the other two authorization phases is part of a resume.
        Assert.Equal(0, adapter.ManualAuthorizationCalls);
        Assert.Equal(0, adapter.ScheduledAuthorizationCalls);
        Assert.Equal(1, adapter.ResumeAuthorizationCalls);

        // The sequence used ONCE: one gate phase, one authorization, one
        // preparation.
        Assert.Equal(3, adapter.Calls.Count);
    }

    [Fact]
    public void Every_declared_trigger_invokes_exactly_its_own_authorization_member()
    {
        // EXHAUSTIVE over the enum rather than over a hand-written list, so adding
        // a trigger without deciding its routing fails here.
        CookTriggerKind[] declared = Enum.GetValues<CookTriggerKind>();
        Assert.Equal(declared.Length, AuthorizationMemberForTrigger.Count);

        foreach (CookTriggerKind trigger in declared)
        {
            string expected = AuthorizationMemberForTrigger[trigger];
            RecordingAdapter adapter = new();

            CookPreparationSequence.Execute(trigger, adapter);

            Assert.Equal(
                new[] { "EvaluateGatesPhase", expected, "PreparePhase" },
                adapter.Calls);

            // And NONE of the other authorization members was touched.
            foreach (string other in AuthorizationMemberForTrigger.Values.Where(m => m != expected))
            {
                Assert.DoesNotContain(other, adapter.Calls);
            }

            // Exactly one authorization call in total, across all three members.
            Assert.Equal(
                1,
                adapter.ManualAuthorizationCalls +
                adapter.ScheduledAuthorizationCalls +
                adapter.ResumeAuthorizationCalls);
        }
    }

    [Fact]
    public void No_trigger_other_than_manual_can_fall_through_to_manual_authorization()
    {
        // The binary fallback this replaced ("scheduled ? scheduled : manual")
        // would have routed Resume - and every future trigger - into MANUAL
        // authorization. The landmine adapter turns that silent mis-route into a
        // thrown exception.
        foreach (CookTriggerKind trigger in Enum.GetValues<CookTriggerKind>()
                     .Where(t => t != CookTriggerKind.Manual))
        {
            ManualAuthorizationLandmineAdapter adapter = new();

            CookPreparationOutcome outcome =
                CookPreparationSequence.Execute(trigger, adapter);

            Assert.Equal(CookPreparationDisposition.Prepared, outcome.Disposition);
            Assert.DoesNotContain("AuthorizeManualPhase", adapter.Calls);
        }

        // POSITIVE CONTROL: the landmine really does detonate, so "it did not
        // throw" above is a real finding rather than an inert adapter.
        ManualAuthorizationLandmineAdapter manual = new();
        Assert.Throws<InvalidOperationException>(
            () => CookPreparationSequence.Execute(CookTriggerKind.Manual, manual));
        Assert.Contains("AuthorizeManualPhase", manual.Calls);
    }

    [Fact]
    public void Scheduled_authorization_is_invoked_exactly_once()
    {
        // The desktop scheduled authorization CONSUMES the skip-next-bake marker.
        // Two invocations would silently eat two occurrences of one skipped Bake.
        RecordingAdapter adapter = new();

        CookPreparationSequence.Execute(CookTriggerKind.Scheduled, adapter);

        Assert.Equal(1, adapter.ScheduledAuthorizationCalls);
        Assert.Equal(1, adapter.Calls.Count(c => c == "AuthorizeScheduledPhase"));

        // POSITIVE CONTROL: the counter is live and can read a value other than 1.
        adapter.AuthorizeScheduledPhase();
        Assert.Equal(2, adapter.ScheduledAuthorizationCalls);
    }

    [Fact]
    public void The_adapter_cannot_reorder_the_phases()
    {
        RecordingAdapter adapter = new();

        CookPreparationSequence.Execute(CookTriggerKind.Scheduled, adapter);

        // The adapter's preferred order is the exact reverse of the real one and is
        // ignored: the sequence, not the adapter, owns phase order.
        Assert.NotEqual(adapter.PreferredOrder, adapter.Calls);
        Assert.Equal(
            new[] { "EvaluateGatesPhase", "AuthorizeScheduledPhase", "PreparePhase" },
            adapter.Calls);

        // POSITIVE CONTROL: the recorder CAN observe the adapter's preferred order,
        // so "the sequence did not follow it" is a real finding rather than a
        // recorder that can only ever produce one list.
        RecordingAdapter direct = new();
        direct.PreparePhase();
        direct.AuthorizeScheduledPhase();
        direct.EvaluateGatesPhase();
        Assert.Equal(direct.PreferredOrder, direct.Calls);
    }

    // ---- fail closed ---------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(99)]
    [InlineData(-1)]
    public void An_undeclared_trigger_fails_closed_and_invokes_no_phase(int rawTrigger)
    {
        RecordingAdapter adapter = new();

        // The raw value really is outside the declared set - otherwise the rest of
        // this test would be asserting the wrong thing.
        Assert.False(Enum.IsDefined((CookTriggerKind)rawTrigger));

        CookPreparationOutcome outcome =
            CookPreparationSequence.Execute((CookTriggerKind)rawTrigger, adapter);

        Assert.Equal(CookPreparationDisposition.RefusedUnsupportedTrigger, outcome.Disposition);
        Assert.Equal(CookPreparationPhase.None, outcome.FurthestPhaseInvoked);
        Assert.False(outcome.Prepared);
        Assert.Empty(adapter.Calls);

        // POSITIVE CONTROL: the SAME adapter instance does record calls when the
        // trigger is one of the two declared kinds, so "Empty" is meaningful.
        CookPreparationSequence.Execute(CookTriggerKind.Manual, adapter);
        Assert.NotEmpty(adapter.Calls);
    }

    [Fact]
    public void The_default_trigger_and_the_default_disposition_are_both_fail_closed()
    {
        RecordingAdapter adapter = new();

        // default(CookTriggerKind) is deliberately not a declared kind.
        Assert.False(Enum.IsDefined(default(CookTriggerKind)));
        Assert.Equal(
            CookPreparationDisposition.RefusedUnsupportedTrigger,
            CookPreparationSequence.Execute(default, adapter).Disposition);

        // default(CookPreparationOutcome) is a refusal, never a success.
        CookPreparationOutcome zero = default;
        Assert.False(zero.Prepared);
        Assert.Equal(CookPreparationDisposition.RefusedUnsupportedTrigger, zero.Disposition);
        Assert.Equal(CookPreparationPhase.None, zero.FurthestPhaseInvoked);
    }

    [Fact]
    public void A_missing_adapter_is_rejected_rather_than_treated_as_prepared()
    {
        Assert.Throws<ArgumentNullException>(
            () => CookPreparationSequence.Execute(CookTriggerKind.Manual, null!));
    }

    // ---- refusal short-circuits every later phase ----------------------------

    [Theory]
    [InlineData(CookTriggerKind.Manual)]
    [InlineData(CookTriggerKind.Scheduled)]
    [InlineData(CookTriggerKind.Resume)]
    public void A_gate_refusal_prevents_every_later_adapter_call(CookTriggerKind trigger)
    {
        RecordingAdapter adapter = new(CookPreparationPhase.Gates);

        CookPreparationOutcome outcome =
            CookPreparationSequence.Execute(trigger, adapter);

        Assert.Equal(CookPreparationDisposition.RefusedAtGates, outcome.Disposition);
        Assert.Equal(CookPreparationPhase.Gates, outcome.FurthestPhaseInvoked);
        Assert.Equal(new[] { "EvaluateGatesPhase" }, adapter.Calls);

        // POSITIVE CONTROL: with the refusal removed, the later phases ARE called.
        RecordingAdapter clean = new();
        CookPreparationSequence.Execute(trigger, clean);
        Assert.Equal(3, clean.Calls.Count);
    }

    [Theory]
    [InlineData(CookTriggerKind.Manual, "AuthorizeManualPhase")]
    [InlineData(CookTriggerKind.Scheduled, "AuthorizeScheduledPhase")]
    [InlineData(CookTriggerKind.Resume, "AuthorizeResumePhase")]
    public void An_authorization_refusal_prevents_preparation(CookTriggerKind trigger, string authCall)
    {
        RecordingAdapter adapter = new(CookPreparationPhase.Authorization);

        CookPreparationOutcome outcome =
            CookPreparationSequence.Execute(trigger, adapter);

        Assert.Equal(CookPreparationDisposition.RefusedAtAuthorization, outcome.Disposition);
        Assert.Equal(CookPreparationPhase.Authorization, outcome.FurthestPhaseInvoked);
        Assert.Equal(new[] { "EvaluateGatesPhase", authCall }, adapter.Calls);
        Assert.DoesNotContain("PreparePhase", adapter.Calls);

        // POSITIVE CONTROL: preparation IS reached when authorization proceeds.
        RecordingAdapter clean = new();
        CookPreparationSequence.Execute(trigger, clean);
        Assert.Contains("PreparePhase", clean.Calls);
    }

    [Theory]
    [InlineData(CookTriggerKind.Manual)]
    [InlineData(CookTriggerKind.Scheduled)]
    [InlineData(CookTriggerKind.Resume)]
    public void A_preparation_refusal_is_reported_as_a_preparation_refusal(CookTriggerKind trigger)
    {
        RecordingAdapter adapter = new(CookPreparationPhase.Preparation);

        CookPreparationOutcome outcome =
            CookPreparationSequence.Execute(trigger, adapter);

        Assert.Equal(CookPreparationDisposition.RefusedAtPreparation, outcome.Disposition);
        Assert.Equal(CookPreparationPhase.Preparation, outcome.FurthestPhaseInvoked);
        Assert.False(outcome.Prepared);
        Assert.Equal(3, adapter.Calls.Count);
    }

    // ---- bounded surface, enforced by SHAPE ----------------------------------

    // Object / dynamic / string / exception / stream / delegate / array payloads
    // are not merely discouraged by comment: no public member of the portable
    // contract is ALLOWED to mention a type outside this closed set.
    private static readonly Type[] PortableTypes =
    {
        typeof(CookPreparationSequence),
        typeof(ICookPreparationAdapter),
        typeof(CookPreparationOutcome),
        typeof(CookTriggerKind),
        typeof(CookPreparationPhase),
        typeof(CookPreparationDisposition),
    };

    // Compiler-synthesised members every record/struct/enum inherits. They are
    // excluded because they are not part of the authored surface.
    private static readonly HashSet<string> SynthesisedMembers = new()
    {
        "Equals", "GetHashCode", "ToString", "op_Equality", "op_Inequality", "PrintMembers",
    };

    [Fact]
    public void The_portable_surface_mentions_no_type_outside_the_closed_set()
    {
        var allowed = new HashSet<Type>(PortableTypes) { typeof(void), typeof(bool), typeof(int) };
        var offenders = new List<string>();

        foreach (Type t in PortableTypes.Where(t => !t.IsEnum))
        {
            foreach (MethodBase m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Cast<MethodBase>()
                         .Concat(t.GetConstructors()))
            {
                if (SynthesisedMembers.Contains(m.Name))
                {
                    continue;
                }

                foreach (ParameterInfo p in m.GetParameters())
                {
                    // A record's Deconstruct uses `out` parameters, whose reflected
                    // type is the by-ref form; unwrap it so the element type is judged.
                    Type pt = Unwrap(p.ParameterType);
                    if (!allowed.Contains(pt))
                    {
                        offenders.Add(t.Name + "." + m.Name + " parameter " + pt.FullName);
                    }
                }

                if (m is MethodInfo mi && !allowed.Contains(Unwrap(mi.ReturnType)))
                {
                    offenders.Add(t.Name + "." + m.Name + " returns " + mi.ReturnType.FullName);
                }
            }
        }

        Assert.Empty(offenders);

        // POSITIVE CONTROL: the detector fires on a member that DOES expose object.
        Assert.DoesNotContain(typeof(object), allowed);
        Assert.DoesNotContain(typeof(string), allowed);
        MethodInfo objectSurface = typeof(CookPreparationSequenceTests)
            .GetMethod(nameof(ControlMemberThatExposesObject), BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.DoesNotContain(Unwrap(objectSurface.GetParameters()[0].ParameterType), allowed);
        Assert.DoesNotContain(Unwrap(objectSurface.ReturnType), allowed);
    }

    private static Type Unwrap(Type t) => t.IsByRef ? t.GetElementType()! : t;

    // Exists only as the positive control for the shape detector above.
    private static object ControlMemberThatExposesObject(object payload) => payload;

    [Fact]
    public void The_trigger_kinds_are_exactly_manual_scheduled_and_resume()
    {
        Assert.Equal(
            new[] { "Manual", "Resume", "Scheduled" },
            Enum.GetNames<CookTriggerKind>().OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.Equal(
            new[] { "Authorization", "Gates", "None", "Preparation" },
            Enum.GetNames<CookPreparationPhase>().OrderBy(n => n, StringComparer.Ordinal).ToArray());

        Assert.Equal(
            new[]
            {
                "Prepared", "RefusedAtAuthorization", "RefusedAtGates",
                "RefusedAtPreparation", "RefusedUnsupportedTrigger",
            },
            Enum.GetNames<CookPreparationDisposition>().OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // The adapter surface is exactly the four phase behaviours - no escape
        // hatch, no step list a host could enumerate, reorder, or skip.
        Assert.Equal(
            new[]
            {
                "AuthorizeManualPhase", "AuthorizeResumePhase", "AuthorizeScheduledPhase",
                "EvaluateGatesPhase", "PreparePhase",
            },
            typeof(ICookPreparationAdapter).GetMethods()
                .Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }
}
