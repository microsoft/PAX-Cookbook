using System;
using System.Collections.Generic;
using PAXCookbook.App;
using Xunit;

namespace PAXCookbook.App.Tests;

// cycle-02r5b — pure, total, fail-closed MSAL-failure classifier.
//
// These deterministic in-memory tests prove WamServiceFailureClassifier maps
// bounded, non-sensitive inputs to exactly one bounded WamAcquireStatus with the
// required security properties:
//   * a REACHED service (HTTP response / status present) is NEVER connectivity;
//   * recognized public AADSTS codes route to authority_registration_mismatch or
//     consent_required; any other reached response fails closed to service_rejected;
//   * connectivity_failure is reachable ONLY from a genuine no-response transport;
//   * broker / configuration / client / unknown shapes are never connectivity;
//   * cancellation is distinct from any failure/denial;
//   * ParseErrorCodes extracts ONLY the integer error_codes and never any other
//     body field, failing closed to empty on missing / malformed / non-array input.
//
// The classifier references no MSAL type and no token/claim material, so these
// tests run in BOTH the stable (ZERO-MSAL) and experimental configurations.
public sealed class WamServiceFailureClassifierTests
{
    private static WamFailureSignal Reached(
        WamFailureKind kind,
        int? statusCode,
        IReadOnlyList<int> errorCodes) =>
        new(kind, HasHttpResponse: true, statusCode, errorCodes, MsalErrorCode: null, GenuineNoResponseTransport: false);

    // ---- reached service: authority / registration mismatch -------------

    [Theory]
    [InlineData(50194)]
    [InlineData(90130)]
    [InlineData(700016)]
    [InlineData(50020)]
    public void ReachedService_AuthorityCodes_MapToAuthorityRegistrationMismatch(int code)
    {
        var signal = Reached(WamFailureKind.Service, statusCode: 400, new[] { code });
        Assert.Equal(WamAcquireStatus.AuthorityRegistrationMismatch, WamServiceFailureClassifier.Classify(signal));
    }

    // ---- reached service: consent required ------------------------------

    [Theory]
    [InlineData(65001)]
    [InlineData(90094)]
    [InlineData(65004)]
    public void ReachedService_ConsentCodes_MapToConsentRequired(int code)
    {
        var signal = Reached(WamFailureKind.Service, statusCode: 403, new[] { code });
        Assert.Equal(WamAcquireStatus.ConsentRequired, WamServiceFailureClassifier.Classify(signal));
    }

    // ---- reached service: any other code -> service_rejected ------------

    [Fact]
    public void ReachedService_UnrecognizedCode_FailsClosedToServiceRejected()
    {
        var signal = Reached(WamFailureKind.Service, statusCode: 400, new[] { 12345 });
        Assert.Equal(WamAcquireStatus.ServiceRejected, WamServiceFailureClassifier.Classify(signal));
    }

    [Fact]
    public void ReachedService_EmptyCodes_FailsClosedToServiceRejected()
    {
        var signal = Reached(WamFailureKind.Service, statusCode: 500, Array.Empty<int>());
        Assert.Equal(WamAcquireStatus.ServiceRejected, WamServiceFailureClassifier.Classify(signal));
    }

    // ---- reached service is NEVER connectivity --------------------------

    [Fact]
    public void ReachedService_WithHttpResponse_IsNeverConnectivity()
    {
        // Even a service-kind exception carrying an HTTP response is a rejection,
        // not a network drop.
        var withStatus = Reached(WamFailureKind.Service, statusCode: 502, Array.Empty<int>());
        Assert.NotEqual(WamAcquireStatus.ConnectivityFailure, WamServiceFailureClassifier.Classify(withStatus));

        // HasHttpResponse true with no status code is still a reached service.
        var responseOnly = new WamFailureSignal(
            WamFailureKind.Service, HasHttpResponse: true, StatusCode: null,
            ErrorCodes: Array.Empty<int>(), MsalErrorCode: null, GenuineNoResponseTransport: false);
        Assert.NotEqual(WamAcquireStatus.ConnectivityFailure, WamServiceFailureClassifier.Classify(responseOnly));
        Assert.Equal(WamAcquireStatus.ServiceRejected, WamServiceFailureClassifier.Classify(responseOnly));
    }

    [Fact]
    public void ServiceKind_NoResponse_FailsClosedToServiceRejected_NotConnectivity()
    {
        // A service-class exception with NO response and NO proven network drop
        // still fails closed to a reached-service rejection, never connectivity.
        var signal = new WamFailureSignal(
            WamFailureKind.Service, HasHttpResponse: false, StatusCode: null,
            ErrorCodes: Array.Empty<int>(), MsalErrorCode: null, GenuineNoResponseTransport: false);
        Assert.Equal(WamAcquireStatus.ServiceRejected, WamServiceFailureClassifier.Classify(signal));
    }

    // ---- connectivity ONLY from a genuine no-response transport ---------

    [Fact]
    public void GenuineNoResponseTransport_MapsToConnectivityFailure()
    {
        var signal = new WamFailureSignal(
            WamFailureKind.Client, HasHttpResponse: false, StatusCode: null,
            ErrorCodes: Array.Empty<int>(), MsalErrorCode: null, GenuineNoResponseTransport: true);
        Assert.Equal(WamAcquireStatus.ConnectivityFailure, WamServiceFailureClassifier.Classify(signal));
    }

    [Fact]
    public void GenuineNoResponseTransport_DoesNotOverrideReachedService()
    {
        // If a service was reached, the reached-service branch wins even if a
        // transport flag is (incorrectly) also set — connectivity requires NO
        // response.
        var signal = new WamFailureSignal(
            WamFailureKind.Service, HasHttpResponse: true, StatusCode: 400,
            ErrorCodes: new[] { 50194 }, MsalErrorCode: null, GenuineNoResponseTransport: true);
        Assert.Equal(WamAcquireStatus.AuthorityRegistrationMismatch, WamServiceFailureClassifier.Classify(signal));
    }

    // ---- broker / configuration / client / unknown are never connectivity

    [Fact]
    public void BrokerKind_NoResponse_MapsToBrokerFailure_NotConnectivity()
    {
        var signal = new WamFailureSignal(
            WamFailureKind.Broker, HasHttpResponse: false, StatusCode: null,
            ErrorCodes: Array.Empty<int>(), MsalErrorCode: null, GenuineNoResponseTransport: false);
        Assert.Equal(WamAcquireStatus.BrokerFailure, WamServiceFailureClassifier.Classify(signal));
    }

    [Fact]
    public void ConfigurationKind_NoResponse_MapsToConfigurationFailure_NotConnectivity()
    {
        var signal = new WamFailureSignal(
            WamFailureKind.Configuration, HasHttpResponse: false, StatusCode: null,
            ErrorCodes: Array.Empty<int>(), MsalErrorCode: null, GenuineNoResponseTransport: false);
        Assert.Equal(WamAcquireStatus.ConfigurationFailure, WamServiceFailureClassifier.Classify(signal));
    }

    [Fact]
    public void ClientKind_NoResponse_MapsToConfigurationFailure_NotConnectivity()
    {
        var signal = new WamFailureSignal(
            WamFailureKind.Client, HasHttpResponse: false, StatusCode: null,
            ErrorCodes: Array.Empty<int>(), MsalErrorCode: null, GenuineNoResponseTransport: false);
        Assert.Equal(WamAcquireStatus.ConfigurationFailure, WamServiceFailureClassifier.Classify(signal));
    }

    [Fact]
    public void UnknownKind_NoResponse_FailsClosedToUnknownFailure_NotConnectivity()
    {
        var signal = new WamFailureSignal(
            WamFailureKind.Unknown, HasHttpResponse: false, StatusCode: null,
            ErrorCodes: Array.Empty<int>(), MsalErrorCode: null, GenuineNoResponseTransport: false);
        Assert.Equal(WamAcquireStatus.UnknownFailure, WamServiceFailureClassifier.Classify(signal));
    }

    // ---- cancellation is distinct ---------------------------------------

    [Fact]
    public void CancelledKind_MapsToUserCancelled_NotFailureOrDenial()
    {
        var signal = new WamFailureSignal(
            WamFailureKind.Cancelled, HasHttpResponse: false, StatusCode: null,
            ErrorCodes: Array.Empty<int>(), MsalErrorCode: null, GenuineNoResponseTransport: false);
        WamAcquireStatus status = WamServiceFailureClassifier.Classify(signal);
        Assert.Equal(WamAcquireStatus.UserCancelled, status);
        Assert.NotEqual(WamAcquireStatus.ServiceRejected, status);
        Assert.NotEqual(WamAcquireStatus.UnknownFailure, status);
    }

    // ---- Classify is total: every kind yields a defined status ----------

    [Fact]
    public void Classify_IsTotal_EveryKindYieldsDefinedStatus()
    {
        foreach (WamFailureKind kind in Enum.GetValues<WamFailureKind>())
        {
            var noResponse = new WamFailureSignal(
                kind, HasHttpResponse: false, StatusCode: null,
                ErrorCodes: Array.Empty<int>(), MsalErrorCode: null, GenuineNoResponseTransport: false);
            Assert.True(Enum.IsDefined(WamServiceFailureClassifier.Classify(noResponse)));

            var reached = new WamFailureSignal(
                kind, HasHttpResponse: true, StatusCode: 400,
                ErrorCodes: Array.Empty<int>(), MsalErrorCode: null, GenuineNoResponseTransport: false);
            Assert.True(Enum.IsDefined(WamServiceFailureClassifier.Classify(reached)));
        }
    }

    // ---- ParseErrorCodes: extracts ONLY integer error_codes -------------

    [Fact]
    public void ParseErrorCodes_ExtractsIntegerCodes()
    {
        const string body = "{\"error\":\"invalid_grant\",\"error_codes\":[50194,90130],\"trace_id\":\"abc\"}";
        IReadOnlyList<int> codes = WamServiceFailureClassifier.ParseErrorCodes(body);
        Assert.Equal(new[] { 50194, 90130 }, codes);
    }

    [Fact]
    public void ParseErrorCodes_IgnoresNonIntegerElements()
    {
        const string body = "{\"error_codes\":[65001,\"x\",true,null,90094]}";
        IReadOnlyList<int> codes = WamServiceFailureClassifier.ParseErrorCodes(body);
        Assert.Equal(new[] { 65001, 90094 }, codes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ malformed")]
    [InlineData("[1,2,3]")]                             // top-level not an object
    [InlineData("{\"error\":\"invalid_grant\"}")]      // no error_codes
    [InlineData("{\"error_codes\":\"50194\"}")]          // error_codes not an array
    [InlineData("{\"error_codes\":{}}")]                 // error_codes not an array
    public void ParseErrorCodes_MissingOrMalformed_FailsClosedToEmpty(string? body)
    {
        Assert.Empty(WamServiceFailureClassifier.ParseErrorCodes(body));
    }

    [Fact]
    public void ParseErrorCodes_MalformedBody_ReachedService_ClassifiesAsServiceRejected()
    {
        // A reached service whose body cannot be parsed still classifies as a
        // reached-service rejection (service_rejected), never connectivity.
        IReadOnlyList<int> codes = WamServiceFailureClassifier.ParseErrorCodes("{ not valid");
        var signal = Reached(WamFailureKind.Service, statusCode: 400, codes);
        Assert.Equal(WamAcquireStatus.ServiceRejected, WamServiceFailureClassifier.Classify(signal));
    }

    [Fact]
    public void ParseErrorCodes_ReturnsOnlyInts_NoOtherBodyFieldEscapes()
    {
        // The parser exposes only ints; no string/identifier field can leak through
        // its return type (IReadOnlyList<int>). This asserts the extracted set holds
        // exactly the declared codes and nothing else.
        const string body = "{\"error\":\"invalid_grant\",\"error_description\":\"AADSTS50194: secret detail\"," +
                            "\"correlation_id\":\"11111111-1111-1111-1111-111111111111\",\"error_codes\":[50194]}";
        IReadOnlyList<int> codes = WamServiceFailureClassifier.ParseErrorCodes(body);
        Assert.Equal(new[] { 50194 }, codes);
    }
}
