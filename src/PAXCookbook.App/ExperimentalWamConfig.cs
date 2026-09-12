using System;

namespace PAXCookbook.App;

// Experimental Entra WAM configuration surface (Track 1 / T1-S2A).
//
// This is the ONLY gate that can make the experimental Entra WAM provider
// selectable. It is disabled by default and fails closed: unless an explicit
// experimental runtime configuration supplies a complete, well-formed set of
// values, the options resolve to Disabled and the provider is never selectable.
//
// Doctrine (binding):
//   - Windows Hello is unaffected when this gate is absent or false.
//   - The provider identifier is exact and closed ("entra-wam").
//   - Tenant and client values are RUNTIME-INJECTED. No registration
//     identifier (tenant/client/object) is ever hardcoded here, in any other
//     source file, in a default, a test, a log, or a generated asset.
//   - Partial or malformed configuration is REJECTED (fails closed), never
//     silently completed with a default.
//   - Stable / customer builds neither enable nor advertise the provider. This
//     type carries no production Settings text and no public documentation.
//
// The values are supplied by the host at construction time (for example from an
// explicit experimental runtime configuration channel). This type performs the
// deterministic parse/validation only; it does not read the environment itself,
// so the parsing is trivially unit-testable with no ambient state.
internal sealed class ExperimentalWamOptions
{
    // The one and only experimental provider identifier. Exact and closed.
    internal const string EntraWamProviderId = "entra-wam";

    // The single delegated Microsoft Graph sign-in permission requested by the
    // native WAM flow. Per the bounded token / photo doctrine, this permission is
    // used to assert identity for session unlock and, ONLY inside the WAM-owning
    // native process, for the single exact GET /v1.0/me/photo/$value profile-photo
    // request; it is never used for audit, directory, managed-key,
    // permission-profile, PAX/Bake, or any other tenant-data query, and the token
    // never leaves that native process.
    internal const string GraphUserReadScope = "User.Read";

    // Active durable configuration schema version. The superseded two-registration
    // model was schema 1; this one-registration model is schema 2. An older
    // schema is treated as migration-required, never silently reinterpreted.
    internal const int SchemaVersion = 2;

    private ExperimentalWamOptions(bool enabled, string tenantId, string clientId)
    {
        Enabled = enabled;
        TenantId = tenantId;
        ClientId = clientId;
    }

    // Disabled, fully-closed default. This is what every stable/customer build
    // and every unconfigured experimental build resolves to.
    internal static ExperimentalWamOptions Disabled { get; } =
        new(enabled: false, tenantId: string.Empty, clientId: string.Empty);

    internal bool Enabled { get; }

    // Runtime-injected tenant and public-client identifiers. Empty unless a
    // complete, well-formed configuration was accepted. Never logged.
    internal string TenantId { get; }

    internal string ClientId { get; }

    // The exact delegated sign-in scope requested from Microsoft Graph.
    internal string RequestScope => GraphUserReadScope;

    // True only when the provider is enabled AND both identifiers are present
    // and well-formed GUIDs. The one-registration model has no resource
    // registration and no distinctness requirement.
    internal bool IsFullyConfigured =>
        Enabled &&
        IsWellFormedGuid(TenantId) &&
        IsWellFormedGuid(ClientId);

    // Deterministic parse of an explicit experimental configuration. Returns
    // Disabled (fails closed) for any of:
    //   - null raw input,
    //   - enabled flag not explicitly true,
    //   - missing/blank tenant or client,
    //   - malformed (non-GUID) tenant or client,
    //   - a provider id other than the exact "entra-wam".
    // Never throws; never fills a missing value with a default.
    internal static ExperimentalWamOptions Create(ExperimentalWamConfigInput? raw)
    {
        if (raw is null)
        {
            return Disabled;
        }

        // The provider id, when supplied, must be exactly the closed value.
        if (raw.ProviderId is not null &&
            !string.Equals(raw.ProviderId, EntraWamProviderId, StringComparison.Ordinal))
        {
            return Disabled;
        }

        if (!raw.Enabled)
        {
            return Disabled;
        }

        string tenant = (raw.TenantId ?? string.Empty).Trim();
        string client = (raw.ClientId ?? string.Empty).Trim();

        if (!IsWellFormedGuid(tenant) || !IsWellFormedGuid(client))
        {
            return Disabled;
        }

        // Enabled path requires the exact provider id (not merely unspecified).
        if (!string.Equals(raw.ProviderId, EntraWamProviderId, StringComparison.Ordinal))
        {
            return Disabled;
        }

        return new ExperimentalWamOptions(enabled: true, tenantId: tenant, clientId: client);
    }

    private static bool IsWellFormedGuid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Exact canonical 36-char hyphenated "D" form only.
        return Guid.TryParseExact(value, "D", out _);
    }
}

// Raw, untyped experimental configuration input. The host maps its explicit
// experimental runtime configuration onto this shape; ExperimentalWamOptions
// performs the deterministic validation. Kept separate so the validation has no
// ambient/environment dependency and is fully unit-testable.
internal sealed class ExperimentalWamConfigInput
{
    internal bool Enabled { get; init; }

    internal string? ProviderId { get; init; }

    internal string? TenantId { get; init; }

    internal string? ClientId { get; init; }
}
