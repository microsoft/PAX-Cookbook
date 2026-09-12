using System;
using System.Collections.Generic;
using System.Reflection;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// cycle-02r5 Batch 1 — bounded, fail-closed work-account rejection-reason chain.
//
// These deterministic tests prove the pure mapping is total + fail-closed, the
// terminal tombstone carries ONLY the bounded reason (and remains authority-free
// under reflection over the REAL record), ApplyNativeResult records the correct
// reason per category while granting nothing on every non-approved path,
// PruneExpired yields Expired, and HandleStatus emits { state, reason } with the
// bounded lowercase snake_case string (unknown request fails closed).
public sealed class ExperimentalWamRejectionReasonTests
{
    private const string TestTenant = "11111111-1111-1111-1111-111111111111";
    private const string TestClient = "22222222-2222-2222-2222-222222222222";

    private static ExperimentalWamOptions Options() =>
        ExperimentalWamOptions.Create(new ExperimentalWamConfigInput
        {
            Enabled = true,
            ProviderId = "entra-wam",
            TenantId = TestTenant,
            ClientId = TestClient,
        });

    // ---- pure mapping: NeutralWamCategory -> bounded reason (total) ------
    // Internal enums cannot appear in public [Theory] signatures (CS0051), so the
    // exhaustive per-value tables live inside the method body.

    [Fact]
    public void FromCategory_MapsEveryDefinedValue()
    {
        var expected = new Dictionary<NeutralWamCategory, WamRejectionReason>
        {
            [NeutralWamCategory.Approved] = WamRejectionReason.None,
            [NeutralWamCategory.Cancelled] = WamRejectionReason.Cancelled,
            [NeutralWamCategory.IdentityFailure] = WamRejectionReason.IdentityFailure,
            [NeutralWamCategory.ScopeFailure] = WamRejectionReason.ScopeFailure,
            [NeutralWamCategory.ConfigurationFailure] = WamRejectionReason.ConfigurationFailure,
            [NeutralWamCategory.BrokerFailure] = WamRejectionReason.BrokerFailure,
            [NeutralWamCategory.TransportFailure] = WamRejectionReason.TransportFailure,
            [NeutralWamCategory.Disabled] = WamRejectionReason.Disabled,
            [NeutralWamCategory.Denied] = WamRejectionReason.Denied,
            // cycle-02r5b bounded MSAL-failure categories.
            [NeutralWamCategory.ConnectivityFailure] = WamRejectionReason.ConnectivityFailure,
            [NeutralWamCategory.AuthorityRegistrationMismatch] = WamRejectionReason.AuthorityRegistrationMismatch,
            [NeutralWamCategory.ServiceRejected] = WamRejectionReason.ServiceRejected,
            [NeutralWamCategory.ConsentRequired] = WamRejectionReason.ConsentRequired,
            [NeutralWamCategory.UnknownFailure] = WamRejectionReason.UnknownFailure,
        };

        // Every defined source value is covered by the table AND maps as expected.
        foreach (NeutralWamCategory c in Enum.GetValues<NeutralWamCategory>())
        {
            Assert.True(expected.ContainsKey(c), $"unmapped category {c}");
            Assert.Equal(expected[c], WamRejectionReasonMap.FromCategory(c));
        }
    }

    [Fact]
    public void FromCategory_IsTotal_EveryDefinedValueMapsToDefinedReason()
    {
        foreach (NeutralWamCategory c in Enum.GetValues<NeutralWamCategory>())
        {
            WamRejectionReason mapped = WamRejectionReasonMap.FromCategory(c);
            Assert.True(Enum.IsDefined(mapped), $"category {c} mapped to undefined reason {mapped}");
        }
    }

    [Fact]
    public void FromCategory_UnknownValue_FailsClosedToDenied()
    {
        Assert.Equal(WamRejectionReason.Denied, WamRejectionReasonMap.FromCategory((NeutralWamCategory)999));
        Assert.Equal(WamRejectionReason.Denied, WamRejectionReasonMap.FromCategory((NeutralWamCategory)(-7)));
    }

    // ---- pure mapping: DaemonWamReason -> bounded reason (total) ---------

    [Fact]
    public void FromDaemonReason_MapsEveryDefinedValue()
    {
        var expected = new Dictionary<DaemonWamReason, WamRejectionReason>
        {
            [DaemonWamReason.Approved] = WamRejectionReason.None,
            [DaemonWamReason.NotConfigured] = WamRejectionReason.ConfigurationFailure,
            [DaemonWamReason.RequestNotFound] = WamRejectionReason.Denied,
            [DaemonWamReason.ChallengeRejected] = WamRejectionReason.Denied,
            [DaemonWamReason.NeutralResultRejected] = WamRejectionReason.Denied,
        };

        foreach (DaemonWamReason r in Enum.GetValues<DaemonWamReason>())
        {
            Assert.True(expected.ContainsKey(r), $"unmapped daemon reason {r}");
            Assert.Equal(expected[r], WamRejectionReasonMap.FromDaemonReason(r));
        }
    }

    [Fact]
    public void FromDaemonReason_IsTotal_EveryDefinedValueMapsToDefinedReason()
    {
        foreach (DaemonWamReason r in Enum.GetValues<DaemonWamReason>())
        {
            WamRejectionReason mapped = WamRejectionReasonMap.FromDaemonReason(r);
            Assert.True(Enum.IsDefined(mapped), $"daemon reason {r} mapped to undefined reason {mapped}");
        }
    }

    [Fact]
    public void FromDaemonReason_UnknownValue_FailsClosedToDenied()
    {
        Assert.Equal(WamRejectionReason.Denied, WamRejectionReasonMap.FromDaemonReason((DaemonWamReason)999));
    }

    // ---- pure mapping: reason -> stable snake_case wire string (total) ---

    [Fact]
    public void ToStatusString_MapsEveryDefinedReason()
    {
        var expected = new Dictionary<WamRejectionReason, string>
        {
            [WamRejectionReason.None] = "none",
            [WamRejectionReason.Cancelled] = "cancelled",
            [WamRejectionReason.IdentityFailure] = "identity_failure",
            [WamRejectionReason.ScopeFailure] = "scope_failure",
            [WamRejectionReason.ConfigurationFailure] = "configuration_failure",
            [WamRejectionReason.BrokerFailure] = "broker_failure",
            [WamRejectionReason.TransportFailure] = "transport_failure",
            [WamRejectionReason.Expired] = "expired",
            [WamRejectionReason.Disabled] = "disabled",
            [WamRejectionReason.Denied] = "denied",
            // cycle-02r5b bounded MSAL-failure reasons.
            [WamRejectionReason.ConnectivityFailure] = "connectivity_failure",
            [WamRejectionReason.AuthorityRegistrationMismatch] = "authority_registration_mismatch",
            [WamRejectionReason.ServiceRejected] = "service_rejected",
            [WamRejectionReason.ConsentRequired] = "consent_required",
            [WamRejectionReason.UnknownFailure] = "unknown_failure",
        };

        foreach (WamRejectionReason r in Enum.GetValues<WamRejectionReason>())
        {
            Assert.True(expected.ContainsKey(r), $"unmapped reason {r}");
            Assert.Equal(expected[r], WamRejectionReasonMap.ToStatusString(r));
        }
    }

    [Fact]
    public void ToStatusString_IsTotalAndContainmentSafe()
    {
        // Every rendered string is bounded snake_case and carries no separators or
        // characters that could smuggle an identifier/claim.
        foreach (WamRejectionReason r in Enum.GetValues<WamRejectionReason>())
        {
            string s = WamRejectionReasonMap.ToStatusString(r);
            Assert.Matches("^[a-z_]+$", s);
        }

        // Fail-closed default for an unmapped value.
        Assert.Equal("denied", WamRejectionReasonMap.ToStatusString((WamRejectionReason)999));
    }

    // ---- terminal tombstone: bounded reason + authority-free (real record) --

    private static object? TerminalRecord(ExperimentalWamDaemonEndpoint ep, string requestId)
    {
        FieldInfo f = typeof(ExperimentalWamDaemonEndpoint).GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (System.Collections.IDictionary)f.GetValue(ep)!;
        return dict.Contains(requestId) ? dict[requestId] : null;
    }

    // Reflect the REAL terminal record and prove it exposes NO authority-bearing
    // field/property and retains no non-empty string (mirrors the terminal-authority
    // tests), while the only added state is the bounded enum reason.
    private static void AssertTombstoneAuthorityFreeWithBoundedReason(object? record, WamRejectionReason expectedReason)
    {
        Assert.NotNull(record);
        Type t = record!.GetType();
        Assert.Equal("TerminalTombstone", t.Name);

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        foreach (string authority in new[]
        {
            "Purpose", "RecipeId", "CapturedGeneration", "Challenge", "Salt", "IdentitySalt",
            "Descriptor", "ExpectedSessionFingerprint", "Account", "AccountId", "Tenant",
            "TenantId", "ClientId", "ObjectId", "Upn", "Token", "Scope", "Scopes", "Generation",
        })
        {
            Assert.Null(t.GetField(authority, all));
            Assert.Null(t.GetProperty(authority, all));
        }

        // The ONLY added state is the bounded enum reason; no string field retains data.
        foreach (FieldInfo fld in t.GetFields(all))
        {
            if (fld.FieldType == typeof(string))
            {
                Assert.True(string.IsNullOrEmpty((string?)fld.GetValue(record)),
                    $"terminal record string field {fld.Name} retained data");
            }
        }

        // The reason field is a bounded WamRejectionReason enum only.
        FieldInfo reasonField = Assert.Single(Array.FindAll(t.GetFields(all), x => x.FieldType == typeof(WamRejectionReason)));
        Assert.Equal(expectedReason, (WamRejectionReason)reasonField.GetValue(record)!);
    }

    [Fact]
    public void ApplyNativeResult_NonApproved_RecordsReason_GrantsNothing_AuthorityFree()
    {
        var cases = new (NeutralWamCategory Category, WamRejectionReason Reason, string Wire)[]
        {
            (NeutralWamCategory.Cancelled, WamRejectionReason.Cancelled, "cancelled"),
            (NeutralWamCategory.IdentityFailure, WamRejectionReason.IdentityFailure, "identity_failure"),
            (NeutralWamCategory.ScopeFailure, WamRejectionReason.ScopeFailure, "scope_failure"),
            (NeutralWamCategory.ConfigurationFailure, WamRejectionReason.ConfigurationFailure, "configuration_failure"),
            (NeutralWamCategory.BrokerFailure, WamRejectionReason.BrokerFailure, "broker_failure"),
            (NeutralWamCategory.TransportFailure, WamRejectionReason.TransportFailure, "transport_failure"),
            (NeutralWamCategory.Disabled, WamRejectionReason.Disabled, "disabled"),
            (NeutralWamCategory.Denied, WamRejectionReason.Denied, "denied"),
            // cycle-02r5b bounded MSAL-failure categories.
            (NeutralWamCategory.ConnectivityFailure, WamRejectionReason.ConnectivityFailure, "connectivity_failure"),
            (NeutralWamCategory.AuthorityRegistrationMismatch, WamRejectionReason.AuthorityRegistrationMismatch, "authority_registration_mismatch"),
            (NeutralWamCategory.ServiceRejected, WamRejectionReason.ServiceRejected, "service_rejected"),
            (NeutralWamCategory.ConsentRequired, WamRejectionReason.ConsentRequired, "consent_required"),
            (NeutralWamCategory.UnknownFailure, WamRejectionReason.UnknownFailure, "unknown_failure"),
        };

        foreach ((NeutralWamCategory category, WamRejectionReason expectedReason, string expectedString) in cases)
        {
            int unlocks = 0;
            var ep = new ExperimentalWamDaemonEndpoint(Options(), new WamChallengeStore(),
                new SessionUnlockCoordinator(() => unlocks++));

            string req = ep.Initiate(WamAuthPurpose.SessionUnlock)!;
            DaemonWamOutcome outcome = ep.ApplyNativeResult(NeutralWamResult.Rejected(req, category));

            Assert.False(outcome.Approved);
            Assert.Equal(0, unlocks);

            // The bounded reason is recorded on the REAL terminal record and surfaced.
            ExperimentalWamStatusReport report = ep.GetStatusReport(req);
            Assert.Equal(ExperimentalWamRequestState.Denied, report.State);
            Assert.Equal(expectedReason, report.Reason);
            Assert.Equal(expectedString, WamRejectionReasonMap.ToStatusString(report.Reason));

            AssertTombstoneAuthorityFreeWithBoundedReason(TerminalRecord(ep, req), expectedReason);
        }
    }

    [Fact]
    public void ApplyNativeResult_Approved_ReasonIsNone_UnlocksOnce_AuthorityFree()
    {
        int unlocks = 0;
        var ep = new ExperimentalWamDaemonEndpoint(Options(), new WamChallengeStore(),
            new SessionUnlockCoordinator(() => unlocks++));

        string req = ep.Initiate(WamAuthPurpose.SessionUnlock)!;
        Assert.True(ep.ApplyNativeResult(NeutralWamResult.Approved(req)).Approved);
        Assert.Equal(1, unlocks);

        ExperimentalWamStatusReport report = ep.GetStatusReport(req);
        Assert.Equal(ExperimentalWamRequestState.Approved, report.State);
        Assert.Equal(WamRejectionReason.None, report.Reason);
        AssertTombstoneAuthorityFreeWithBoundedReason(TerminalRecord(ep, req), WamRejectionReason.None);
    }

    // ---- PruneExpired: abandoned pending -> reason Expired ---------------

    [Fact]
    public void PruneExpired_AbandonedPending_YieldsExpiredReason_NoGrant()
    {
        DateTime now = new(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);
        int unlocks = 0;
        var ep = new ExperimentalWamDaemonEndpoint(Options(), new WamChallengeStore(() => now),
            new SessionUnlockCoordinator(() => unlocks++), () => now);

        string req = ep.Initiate(WamAuthPurpose.SessionUnlock)!;
        now = now.AddSeconds(ExperimentalWamDaemonEndpoint.RequestTtlSeconds + 1);

        ExperimentalWamStatusReport report = ep.GetStatusReport(req);
        Assert.Equal(ExperimentalWamRequestState.Denied, report.State);
        Assert.Equal(WamRejectionReason.Expired, report.Reason);
        AssertTombstoneAuthorityFreeWithBoundedReason(TerminalRecord(ep, req), WamRejectionReason.Expired);

        // A late native result still grants nothing.
        Assert.False(ep.ApplyNativeResult(NeutralWamResult.Approved(req)).Approved);
        Assert.Equal(0, unlocks);
    }

    // ---- challenge rejection (replay/generation) -> Denied reason --------

    [Fact]
    public void ApplyNativeResult_Replay_SecondApply_ReasonDenied_NoGrant()
    {
        int unlocks = 0;
        var ep = new ExperimentalWamDaemonEndpoint(Options(), new WamChallengeStore(),
            new SessionUnlockCoordinator(() => unlocks++));

        string req = ep.Initiate(WamAuthPurpose.SessionUnlock)!;
        Assert.True(ep.ApplyNativeResult(NeutralWamResult.Approved(req)).Approved);

        DaemonWamOutcome replay = ep.ApplyNativeResult(NeutralWamResult.Approved(req));
        Assert.False(replay.Approved);
        Assert.Equal(DaemonWamReason.RequestNotFound, replay.Reason);
        Assert.Equal(1, unlocks);

        // The approved tombstone from the first apply still reads reason None; a
        // replay creates no new record and cannot revive authority.
        Assert.Equal(WamRejectionReason.None, ep.GetStatusReport(req).Reason);
    }

    // ---- HandleStatus: { state, reason }, unknown fails closed ----------

    private static (string State, string Reason) StatusStrings(ExperimentalWamDaemonEndpoint ep, string? requestId)
    {
        (int status, object body) = ExperimentalWamRoutes.HandleStatus(ep, requestId);
        Assert.Equal(200, status);
        Type t = body.GetType();
        string state = (string)t.GetProperty("state")!.GetValue(body)!;
        string reason = (string)t.GetProperty("reason")!.GetValue(body)!;
        return (state, reason);
    }

    [Fact]
    public void HandleStatus_Pending_ReturnsPendingAndNone()
    {
        var ep = new ExperimentalWamDaemonEndpoint(Options());
        string req = ep.Initiate(WamAuthPurpose.SessionUnlock)!;
        (string state, string reason) = StatusStrings(ep, req);
        Assert.Equal("Pending", state);
        Assert.Equal("none", reason);
    }

    [Fact]
    public void HandleStatus_DeniedCategory_ReturnsDeniedStateAndBoundedReason()
    {
        var ep = new ExperimentalWamDaemonEndpoint(Options());
        string req = ep.Initiate(WamAuthPurpose.SessionUnlock)!;
        ep.ApplyNativeResult(NeutralWamResult.Rejected(req, NeutralWamCategory.Cancelled));

        (string state, string reason) = StatusStrings(ep, req);
        Assert.Equal("Denied", state);
        Assert.Equal("cancelled", reason);
    }

    [Fact]
    public void HandleStatus_Approved_ReturnsApprovedAndNone()
    {
        var ep = new ExperimentalWamDaemonEndpoint(Options());
        string req = ep.Initiate(WamAuthPurpose.SessionUnlock)!;
        ep.ApplyNativeResult(NeutralWamResult.Approved(req));

        (string state, string reason) = StatusStrings(ep, req);
        Assert.Equal("Approved", state);
        Assert.Equal("none", reason);
    }

    [Theory]
    [InlineData("never-minted-request")]
    [InlineData("")]
    [InlineData(null)]
    public void HandleStatus_UnknownOrAbsentRequest_FailsClosed(string? requestId)
    {
        var ep = new ExperimentalWamDaemonEndpoint(Options());
        (string state, string reason) = StatusStrings(ep, requestId);
        Assert.Equal("Unknown", state);
        Assert.Equal("denied", reason);
    }

    // ---- fail-closed defaults for out-of-range casts (unit-level) --------

    [Fact]
    public void FailClosedDefaults_UnknownCasts_YieldDenied()
    {
        Assert.Equal(WamRejectionReason.Denied, WamRejectionReasonMap.FromCategory((NeutralWamCategory)999));
        Assert.Equal(WamRejectionReason.Denied, WamRejectionReasonMap.FromCategory((NeutralWamCategory)(-7)));
        Assert.Equal(WamRejectionReason.Denied, WamRejectionReasonMap.FromDaemonReason((DaemonWamReason)999));
        Assert.Equal("denied", WamRejectionReasonMap.ToStatusString((WamRejectionReason)999));
    }
}
