using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// T1-S1 — provider-neutral session-unlock contract. These freeze the
// ORCHESTRATION seam introduced by the Hello-preserving refactor using injected
// fakes and spy side effects; they do NOT run a live Windows Hello ceremony
// (impractical to automate) and they do not touch the shared WebAuthn
// verification core, which is unchanged and exercised at runtime.
public sealed class AuthProviderContractsTests
{
    // ---- fakes (exercise orchestration without live WebAuthn) -----------

    private sealed class FakeSessionProvider : ISessionUnlockProvider
    {
        private readonly SessionUnlockOutcome _outcome;
        internal FakeSessionProvider(SessionUnlockOutcome outcome) { _outcome = outcome; }
        public string ProviderId => "fake";
        public SessionUnlockOutcome Authorize() => _outcome;
    }

    // ---- session unlock -------------------------------------------------

    [Fact]
    public void ApprovedSessionVerdict_AppliesUnlockSideEffectExactlyOnce()
    {
        int unlocks = 0;
        var coordinator = new SessionUnlockCoordinator(applyUnlock: () => unlocks++);

        SessionUnlockOutcome outcome = coordinator.Unlock(new FakeSessionProvider(SessionUnlockOutcome.Grant));

        Assert.True(outcome.Approved);
        Assert.Equal(1, unlocks);
    }

    [Fact]
    public void DeniedSessionVerdict_DoesNotUnlock()
    {
        int unlocks = 0;
        var coordinator = new SessionUnlockCoordinator(applyUnlock: () => unlocks++);

        SessionUnlockOutcome outcome = coordinator.Unlock(new FakeSessionProvider(SessionUnlockOutcome.Deny));

        Assert.False(outcome.Approved);
        Assert.Equal(0, unlocks);
    }

    // ---- neutrality of the shared contract ------------------------------

    [Fact]
    public void SharedContractTypes_ExposeNoTransportOrTrustTypes()
    {
        // The provider interface and neutral outcome/decision types must not
        // surface any HTTP/JSON/WebAuthn/OAuth/WAM/MSAL/loopback/identity
        // transport type on their member signatures. Provider-specific detail
        // lives only in the Hello adapters, never in the contract.
        Type[] contractTypes =
        {
            typeof(ISessionUnlockProvider),
            typeof(SessionUnlockOutcome),
            typeof(SessionUnlockDecision),
        };

        string[] forbidden =
        {
            "json", "webauthn", "http", "oauth", "wam", "msal",
            "identity.client", "nonce", "tenant", "loopback", "bearer",
        };

        foreach (Type t in contractTypes)
        {
            foreach (string typeName in MemberTypeNames(t))
            {
                string lower = typeName.ToLowerInvariant();
                Assert.DoesNotContain(forbidden, f => lower.Contains(f));
            }
        }
    }

    private static IEnumerable<string> MemberTypeNames(Type t)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var names = new List<string>();
        foreach (MethodInfo m in t.GetMethods(all))
        {
            names.Add(m.ReturnType.FullName ?? m.ReturnType.Name);
            foreach (ParameterInfo p in m.GetParameters())
            {
                names.Add(p.ParameterType.FullName ?? p.ParameterType.Name);
            }
        }
        foreach (PropertyInfo pr in t.GetProperties(all))
        {
            names.Add(pr.PropertyType.FullName ?? pr.PropertyType.Name);
        }
        return names;
    }
}
