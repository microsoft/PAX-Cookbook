using System.Collections.Generic;
using System.Text.Json;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// PHASE-3 tests for the PHASE-2 attended Hello capability diagnostic sink
// (cycle-01r-hello-capability-probe-repair). MEASUREMENT ONLY.
//
// These assert the STRICT allow-list projection HelloAttendedDiagCapture applies
// to a renderer-supplied payload before anything is ever written to disk: only
// the fixed allow-list keys survive, every bounded enum outside its domain is
// coerced to its safe sentinel (or dropped to null), nullable booleans stay
// tri-state, over-length / non-string values are dropped, and — decisively — no
// unlisted key (an account id, UPN, tenant, client id, token, claim, credential
// id, challenge, attestation, or certificate) can ever survive the projection.
public sealed class HelloAttendedDiagCaptureTests
{
    private static JsonElement Payload(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static readonly string[] ExpectedKeys =
    {
        "schemaVersion",
        "cycleId",
        "capturedUtc",
        "iframeSecureContext",
        "topLevelSecureContext",
        "iframePlatformAuthenticatorAvailable",
        "topLevelPlatformAuthenticatorAvailable",
        "originRelation",
        "enrollmentRequestPosted",
        "enrollmentRequestReceived",
        "enrollmentAffordanceOpened",
        "enrollmentGestureStarted",
        "credentialCeremonyInvoked",
        "createFailureClass",
        "enrollmentResultOutcome",
        "backendSelectAttempted",
        "backendSelectReason",
        "backendSelectPersisted",
        "finalSelectedProvider",
    };

    [Fact]
    public void ProjectAllowList_EmitsExactlyTheAllowListKeys_AndNothingElse()
    {
        // A payload that mixes valid allow-list fields with hostile unlisted keys
        // carrying identity/credential material.
        JsonElement payload = Payload(
            """
            {
              "schemaVersion": "hello-attended-1",
              "cycleId": "cycle-01r-hello-capability-probe-repair",
              "capturedUtc": "2026-07-21T17:00:00.000Z",
              "iframeSecureContext": true,
              "credentialCeremonyInvoked": true,
              "createFailureClass": "not_allowed",
              "finalSelectedProvider": "work_account",
              "accountId": "attacker@contoso.com",
              "tenantId": "11111111-1111-1111-1111-111111111111",
              "clientId": "22222222-2222-2222-2222-222222222222",
              "credentialId": "AAAABBBBCCCCDDDD",
              "challenge": "ZZZZ-challenge-bytes",
              "attestationObject": "o2NmbXQ",
              "token": "eyJhbGciOi",
              "upn": "victim@contoso.onmicrosoft.com"
            }
            """);

        Dictionary<string, object?> result = HelloAttendedDiagCapture.ProjectAllowList(payload);

        // The projected key set is EXACTLY the allow-list — no more, no less.
        Assert.Equal(new HashSet<string>(ExpectedKeys), new HashSet<string>(result.Keys));

        // Every hostile unlisted key was dropped entirely.
        foreach (string leaked in new[]
                 {
                     "accountId", "tenantId", "clientId", "credentialId",
                     "challenge", "attestationObject", "token", "upn",
                 })
        {
            Assert.False(result.ContainsKey(leaked));
        }

        // A whole-object serialization carries none of the hostile values.
        string json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("attacker@contoso.com", json);
        Assert.DoesNotContain("11111111-1111-1111-1111-111111111111", json);
        Assert.DoesNotContain("22222222-2222-2222-2222-222222222222", json);
        Assert.DoesNotContain("AAAABBBBCCCCDDDD", json);
        Assert.DoesNotContain("challenge-bytes", json);
        Assert.DoesNotContain("eyJhbGciOi", json);
        Assert.DoesNotContain("onmicrosoft.com", json);
    }

    [Fact]
    public void ProjectAllowList_PreservesValidBoundedValues()
    {
        JsonElement payload = Payload(
            """
            {
              "schemaVersion": "hello-attended-1",
              "cycleId": "cycle-01r-hello-capability-probe-repair",
              "capturedUtc": "2026-07-21T17:00:00.000Z",
              "iframeSecureContext": true,
              "topLevelSecureContext": true,
              "iframePlatformAuthenticatorAvailable": false,
              "topLevelPlatformAuthenticatorAvailable": true,
              "originRelation": "same_origin",
              "enrollmentRequestPosted": true,
              "enrollmentRequestReceived": true,
              "enrollmentAffordanceOpened": true,
              "enrollmentGestureStarted": true,
              "credentialCeremonyInvoked": true,
              "createFailureClass": "not_allowed",
              "enrollmentResultOutcome": "cancelled",
              "backendSelectAttempted": true,
              "backendSelectReason": "windows_hello_enrollment_required",
              "backendSelectPersisted": false,
              "finalSelectedProvider": "work_account"
            }
            """);

        Dictionary<string, object?> r = HelloAttendedDiagCapture.ProjectAllowList(payload);

        Assert.Equal("hello-attended-1", r["schemaVersion"]);
        Assert.Equal("cycle-01r-hello-capability-probe-repair", r["cycleId"]);
        Assert.Equal(true, r["iframeSecureContext"]);
        Assert.Equal(false, r["iframePlatformAuthenticatorAvailable"]);
        Assert.Equal("same_origin", r["originRelation"]);
        Assert.Equal(true, r["credentialCeremonyInvoked"]);
        Assert.Equal("not_allowed", r["createFailureClass"]);
        Assert.Equal("cancelled", r["enrollmentResultOutcome"]);
        Assert.Equal("windows_hello_enrollment_required", r["backendSelectReason"]);
        Assert.Equal("work_account", r["finalSelectedProvider"]);
    }

    [Theory]
    [InlineData("not_allowed")]
    [InlineData("security")]
    [InlineData("abort")]
    [InlineData("timeout")]
    [InlineData("constraint")]
    [InlineData("unknown")]
    public void ProjectAllowList_AcceptsEveryBoundedCreateFailureClass(string cls)
    {
        JsonElement payload = Payload($$"""{ "createFailureClass": "{{cls}}" }""");
        Dictionary<string, object?> r = HelloAttendedDiagCapture.ProjectAllowList(payload);
        Assert.Equal(cls, r["createFailureClass"]);
    }

    [Fact]
    public void ProjectAllowList_DropsOutOfDomainCreateFailureClassToNull()
    {
        // A create-failure class outside the bounded domain (e.g. a smuggled
        // error message) is dropped to null — never persisted as free text.
        JsonElement payload = Payload(
            """{ "createFailureClass": "NotAllowedError: user@contoso.com denied" }""");
        Dictionary<string, object?> r = HelloAttendedDiagCapture.ProjectAllowList(payload);
        Assert.Null(r["createFailureClass"]);
    }

    [Fact]
    public void ProjectAllowList_CoercesOutOfDomainNonNullableEnumsToSentinel()
    {
        JsonElement payload = Payload(
            """
            {
              "originRelation": "smuggled-origin-string",
              "finalSelectedProvider": "windows_hello_ATTACKER"
            }
            """);
        Dictionary<string, object?> r = HelloAttendedDiagCapture.ProjectAllowList(payload);
        // Both non-nullable bounded enums coerce to their safe sentinel.
        Assert.Equal("unavailable", r["originRelation"]);
        Assert.Equal("unavailable", r["finalSelectedProvider"]);
    }

    [Fact]
    public void ProjectAllowList_DropsOutOfDomainNullableEnumsToNull()
    {
        JsonElement payload = Payload(
            """
            {
              "enrollmentResultOutcome": "definitely-not-a-real-outcome",
              "backendSelectReason": "smuggled reason with PII user@contoso.com"
            }
            """);
        Dictionary<string, object?> r = HelloAttendedDiagCapture.ProjectAllowList(payload);
        Assert.Null(r["enrollmentResultOutcome"]);
        Assert.Null(r["backendSelectReason"]);
    }

    [Fact]
    public void ProjectAllowList_KeepsBooleansTriState_AndDropsNonBooleanToNull()
    {
        JsonElement payload = Payload(
            """
            {
              "iframeSecureContext": true,
              "topLevelSecureContext": false,
              "credentialCeremonyInvoked": "yes-please"
            }
            """);
        Dictionary<string, object?> r = HelloAttendedDiagCapture.ProjectAllowList(payload);
        Assert.Equal(true, r["iframeSecureContext"]);
        Assert.Equal(false, r["topLevelSecureContext"]);
        // A non-boolean JSON value is dropped to null (not coerced to a bool).
        Assert.Null(r["credentialCeremonyInvoked"]);
        // A field entirely absent from the payload is present as null.
        Assert.Null(r["backendSelectPersisted"]);
    }

    [Fact]
    public void ProjectAllowList_DropsOverLengthAndNonStringBoundedStrings()
    {
        string longValue = new string('x', 200);
        JsonElement payload = Payload(
            $$"""
            {
              "schemaVersion": "{{longValue}}",
              "cycleId": 12345,
              "capturedUtc": "2026-07-21T17:00:00.000Z"
            }
            """);
        Dictionary<string, object?> r = HelloAttendedDiagCapture.ProjectAllowList(payload);
        // Over the 128-char cap -> dropped to null.
        Assert.Null(r["schemaVersion"]);
        // Non-string -> dropped to null.
        Assert.Null(r["cycleId"]);
        // Valid short string survives.
        Assert.Equal("2026-07-21T17:00:00.000Z", r["capturedUtc"]);
    }

    [Fact]
    public void TryHandle_ReturnsFalseForForeignEnvelope()
    {
        Assert.False(HelloAttendedDiagCapture.TryHandle(
            """{ "type": "cookbook:some-other-message", "payload": {} }""", outDir: null));
    }

    [Fact]
    public void TryHandle_ReturnsFalseForMalformedJson()
    {
        Assert.False(HelloAttendedDiagCapture.TryHandle("not-json-at-all", outDir: null));
        Assert.False(HelloAttendedDiagCapture.TryHandle(null, outDir: null));
        Assert.False(HelloAttendedDiagCapture.TryHandle("   ", outDir: null));
    }

    [Fact]
    public void TryHandle_ConsumesOwnEnvelopeEvenWithNoOutputDirectory()
    {
        // A well-formed attended envelope is consumed (returns true) so it never
        // falls through to another handler, even when nothing can be persisted.
        Assert.True(HelloAttendedDiagCapture.TryHandle(
            """{ "type": "cookbook:hello-attended-diag-capture", "payload": { "iframeSecureContext": true } }""",
            outDir: null));
    }
}
