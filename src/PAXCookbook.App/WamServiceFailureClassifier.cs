using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PAXCookbook.App;

// cycle-02r5b — DIAGNOSTIC-ONLY bounded MSAL-failure classifier.
//
// This file replaces the former too-broad "every MsalServiceException ->
// TransportFailure" mapping with a PURE, TOTAL, FAIL-CLOSED classifier. It is
// deliberately NOT gated on EXPERIMENTAL_WAM: it references NO MSAL type and NO
// token/claim material, so it compiles into every build (including the stable,
// ZERO-MSAL build) and its unit tests run in both configurations. The thin MSAL
// glue in MsalEntraWamAuthenticator (EXPERIMENTAL_WAM only) extracts the
// non-sensitive inputs below from the caught exception and calls this classifier.
//
// CONTAINMENT INVARIANT: the ONLY things this type ever sees are the bounded,
// non-sensitive extracted inputs on WamFailureSignal. It NEVER receives — and can
// therefore never emit or persist — the raw ResponseBody, Message,
// error_description, CorrelationId, Claims, identifiers, tokens, or the numeric
// AADSTS code as a value. A numeric AADSTS code may enter ONLY as an int inside
// the ErrorCodes array and is used transiently to select a bounded category; only
// the bounded WamAcquireStatus category leaves this classifier.

// The bounded exception SHAPE the glue extracts from a caught MSAL exception.
// This carries no message, host, address, or identifier — only the shape class.
internal enum WamFailureKind
{
    // MsalServiceException — the identity service was contacted and returned an
    // error response (or a service-class error). Reached-service, never a
    // no-network transport condition merely because auth failed.
    Service = 0,

    // A client-side MsalClientException that is neither a documented broker error
    // nor a cancellation (configuration/client-class error).
    Client = 1,

    // A documented WAM/broker error (the broker itself failed to produce a result).
    Broker = 2,

    // A client configuration error (bad configuration reached MSAL at runtime).
    Configuration = 3,

    // The user dismissed / cancelled the account picker or consent prompt.
    Cancelled = 4,

    // Any other unexpected MSAL exception shape. Fails closed to unknown_failure
    // (never connectivity) unless a genuine no-response transport is present.
    Unknown = 5,
}

// The bounded, non-sensitive inputs consumed by the classifier. Every field is a
// shape/flag/number — never a raw string of token, claim, body, correlation id,
// or identifier. MsalErrorCode is a bounded MSAL error CODE string (e.g.
// "invalid_grant"); it is never an error_description, message, or identifier and
// is used only transiently, never emitted.
internal readonly record struct WamFailureSignal(
    WamFailureKind Kind,
    bool HasHttpResponse,
    int? StatusCode,
    IReadOnlyList<int> ErrorCodes,
    string? MsalErrorCode,
    bool GenuineNoResponseTransport);

// Pure, total, fail-closed classifier. Every path returns a bounded
// WamAcquireStatus; connectivity_failure is reachable ONLY from a genuine
// no-response transport condition and NEVER from a reached-service response.
internal static class WamServiceFailureClassifier
{
    // Fixed, PUBLIC AADSTS codes that mean the authority/registration is not
    // compatible with the requested sign-in (unauthorized-for-authority, an
    // out-of-tenant account via /common, or missing-in-directory). These are
    // well-known public numbers, not secrets; they are used only to select a
    // bounded category and never leave as a value.
    private static readonly int[] AuthorityRegistrationMismatchCodes = { 50194, 90130, 700016, 50020 };

    // Fixed, PUBLIC AADSTS codes that mean administrator/user consent is required.
    private static readonly int[] ConsentRequiredCodes = { 65001, 90094, 65004 };

    // Classify a failed MSAL acquisition into exactly one bounded category.
    internal static WamAcquireStatus Classify(WamFailureSignal signal)
    {
        // (cancellation) — distinct from any failure/denial.
        if (signal.Kind == WamFailureKind.Cancelled)
        {
            return WamAcquireStatus.UserCancelled;
        }

        // (1) A REACHED service (an HTTP response / status code is present) is NEVER
        // connectivity, no matter which auth error it carries. Auth failing is not
        // the network being down.
        bool reachedService = signal.HasHttpResponse || signal.StatusCode.HasValue;
        if (reachedService)
        {
            return ClassifyReachedService(signal.ErrorCodes);
        }

        // (6) connectivity_failure is reachable ONLY from a genuine no-response
        // transport condition (an inner socket/http/web failure with no HTTP
        // response, or a documented MSAL no-network error code the glue detected).
        if (signal.GenuineNoResponseTransport)
        {
            return WamAcquireStatus.ConnectivityFailure;
        }

        // No HTTP response AND no proven network drop: bounded strictly by the
        // exception shape, and NEVER connectivity.
        return signal.Kind switch
        {
            // (5) A service-class exception with no parsable response still fails
            // closed to a reached-service rejection — never connectivity.
            WamFailureKind.Service => WamAcquireStatus.ServiceRejected,

            // Documented broker failure.
            WamFailureKind.Broker => WamAcquireStatus.BrokerFailure,

            // (6) configuration/client errors -> configuration_failure (not network).
            WamFailureKind.Configuration => WamAcquireStatus.ConfigurationFailure,
            WamFailureKind.Client => WamAcquireStatus.ConfigurationFailure,

            // (7) Unknown/unmapped conditions fail closed to unknown_failure.
            _ => WamAcquireStatus.UnknownFailure,
        };
    }

    // A reached-service response: choose the bounded category from the structured
    // error_codes. (4) recognized fixed public codes map to authority-mismatch or
    // consent-required; (5) an absent/empty/unrecognized set fails closed to
    // service_rejected — NEVER connectivity.
    private static WamAcquireStatus ClassifyReachedService(IReadOnlyList<int> errorCodes)
    {
        if (ContainsAny(errorCodes, AuthorityRegistrationMismatchCodes))
        {
            return WamAcquireStatus.AuthorityRegistrationMismatch;
        }

        if (ContainsAny(errorCodes, ConsentRequiredCodes))
        {
            return WamAcquireStatus.ConsentRequired;
        }

        return WamAcquireStatus.ServiceRejected;
    }

    private static bool ContainsAny(IReadOnlyList<int> codes, int[] wanted)
    {
        if (codes is null || codes.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < codes.Count; i++)
        {
            for (int j = 0; j < wanted.Length; j++)
            {
                if (codes[i] == wanted[j])
                {
                    return true;
                }
            }
        }

        return false;
    }

    // PURE, testable in-memory extraction of the structured `error_codes` array
    // from an MSAL ResponseBody. Returns ONLY the integer codes — never any other
    // field of the body (error, error_description, correlation_id, trace_id,
    // timestamp, claims). A missing/absent/malformed body, a missing or non-array
    // `error_codes`, or any parse failure yields an EMPTY list, so the caller then
    // classifies the reached-service response as service_rejected (never
    // connectivity). Non-integer array elements are ignored.
    internal static IReadOnlyList<int> ParseErrorCodes(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return Array.Empty<int>();
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<int>();
            }

            if (!doc.RootElement.TryGetProperty("error_codes", out JsonElement codesElement) ||
                codesElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<int>();
            }

            var codes = new List<int>();
            foreach (JsonElement element in codesElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out int code))
                {
                    codes.Add(code);
                }
            }

            return codes;
        }
        catch (JsonException)
        {
            return Array.Empty<int>();
        }
    }
}
