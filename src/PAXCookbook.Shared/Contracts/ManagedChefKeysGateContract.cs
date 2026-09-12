using System;

namespace PAXCookbook.Shared.Contracts;

// Canonical, cross-component contract for the MANAGED CHEF'S KEYS AUTHORIZATION
// GATE (Cycle 04).
//
// This gate answers exactly one question: does the administrator-authored machine
// policy AUTHORIZE a FUTURE organization-provided Chef's Keys inventory to exist?
// It is an authorization/status foundation ONLY. It does NOT implement an
// inventory source, enumerate organization keys, discover/select/validate a
// certificate, access a private key, read a managed-key manifest, obtain a
// service-backed credential, install a service, use a key, write admin policy,
// run an elevated helper, provision a tenant, rotate/remove a key, add a second
// Cook pipeline, or activate ANY capability. `enabled` policy means only
// "future behavior is policy-authorized", never activation or availability.
//
// SOLE INPUT: the typed Cycle 3 `MachinePolicyDetection`. The gate never reparses
// the registry, never reads HKCU, never reads an environment variable, never
// reads a command-line override, never reads LocalAppData/workspace policy, never
// downloads policy, never queries a tenant, never queries Graph, never caches or
// persists policy, never accepts a policy field from an HTTP request or the
// renderer, and never trusts a renderer-supplied authorization state.
//
// LAYERED SEPARATION (being at layer N never implies layer N+1):
//   1 policy authorization  — machine policy permits an org inventory to exist.
//   2 inventory implementation (NOT built here)
//   3 inventory availability   (NEVER true this cycle; InventoryLoaded == false)
//   4 certificate usability    (out of scope)
//   5 Recipe binding           (out of scope)
//   6 recipe run stage         (out of scope; must never trigger elevation)
// The authorized terminal state of THIS cycle is `authorized_not_provisioned`,
// which asserts ONLY layer 1.
//
// SINGLE SOURCE OF TRUTH. Compiled into PAXCookbook.Shared (used by Setup) AND
// linked directly into the native host (PAXCookbook.App, which does not reference
// Shared) via the established <Compile Include=... Link=...> pattern, so both use
// the exact same enum strings and wire tokens with no duplicated constants that
// can drift. It depends only on System (no Win32) so it links cleanly into the
// App and compiles under Shared's plain net8.0 target.
//
// FAIL-CLOSED DOCTRINE (binding): a null detection, an impossible/unsupported
// detection combination, and every invalid/untrusted/inaccessible policy map to
// `Unavailable` with a bounded reason — NEVER to `AuthorizedNotProvisioned` and
// NEVER to a self-service permissive path. Authorization is granted ONLY when the
// typed detection is simultaneously Configured + OrganizationManaged +
// ManagedChefKeys Enabled.

// Ownership tag that keeps a FUTURE organization inventory structurally distinct
// from the per-user, Windows-Credential-Manager-backed personal Chef's Keys. It
// is descriptive only; this cycle stores nothing new and enumerates nothing.
public enum ChefKeyOrigin
{
    Personal,
    Organization,
}

// The ONLY organization-key authentication type that is structurally permitted:
// app-registration certificate. Single-member on purpose — app-registration
// secret, web login, device code, and managed identity are NOT members, so they
// are unrepresentable as an organization auth type (certificate-only; secrets
// prohibited for organization origin).
public enum OrganizationKeyAuthType
{
    AppRegistrationCertificate,
}

// The bounded gate state that may cross the broker/API/UI boundary.
public enum ManagedChefKeysGateState
{
    NotConfigured,
    Disabled,
    AuthorizedNotProvisioned,
    Unavailable,
}

// The bounded reason behind the gate state. No registry path, raw value,
// exception text, identifier, or secret is ever represented here.
public enum ManagedChefKeysGateReason
{
    NotConfigured,
    SelfService,
    DisabledByPolicy,
    AuthorizedNotProvisioned,
    PolicyInvalid,
    PolicyUntrusted,
    PolicyUnavailable,
    Unknown,
}

// An immutable, bounded status projection. Impossible states are made hard to
// represent: a PRIVATE ctor plus one static factory per state, and `Authorized`
// is true ONLY for `AuthorizedNotProvisioned`.
//
// The type carries NO client secret, NO secret-presence boolean, NO UPN, NO
// interactive account data, NO token, NO claim, NO tenant/client/account/
// certificate identifier, NO certificate bytes, NO private-key bytes, NO
// caller-supplied path, and NO executable data — none of those fields exist.
// `InventoryLoaded` is always false (authorization != availability);
// `CertificateOnly` states the FUTURE constraint, not present usability.
public sealed class ManagedChefKeysGateProjection
{
    private ManagedChefKeysGateProjection(
        ManagedChefKeysGateState state,
        ManagedChefKeysGateReason reason,
        bool authorized)
    {
        State = state;
        Reason = reason;
        Authorized = authorized;
    }

    public ManagedChefKeysGateState State { get; }

    public ManagedChefKeysGateReason Reason { get; }

    // True ONLY in the authorized-not-provisioned terminal state. Authorization
    // means "machine policy permits the inventory to exist", never "a key exists".
    public bool Authorized { get; }

    // Bounded FUTURE-constraint markers, constant by construction.
    public bool ReadOnly => true;

    public bool CertificateOnly => true;

    // Availability is NEVER asserted this cycle; a concrete inventory is never
    // connected/loaded, so this is always false.
    public bool InventoryLoaded => false;

    // Denied: absent policy (personal default).
    public static ManagedChefKeysGateProjection NotConfiguredAbsent()
        => new(ManagedChefKeysGateState.NotConfigured, ManagedChefKeysGateReason.NotConfigured, authorized: false);

    // Denied: valid self-service policy.
    public static ManagedChefKeysGateProjection NotConfiguredSelfService()
        => new(ManagedChefKeysGateState.NotConfigured, ManagedChefKeysGateReason.SelfService, authorized: false);

    // Denied: organization-managed but managed Chef's Keys turned off by policy.
    public static ManagedChefKeysGateProjection Disabled()
        => new(ManagedChefKeysGateState.Disabled, ManagedChefKeysGateReason.DisabledByPolicy, authorized: false);

    // Granted (layer 1 only): organization-managed and managed Chef's Keys
    // enabled. No inventory, no certificate access, InventoryLoaded == false.
    public static ManagedChefKeysGateProjection AuthorizedNotProvisioned()
        => new(ManagedChefKeysGateState.AuthorizedNotProvisioned, ManagedChefKeysGateReason.AuthorizedNotProvisioned, authorized: true);

    // Denied, fail-closed: invalid / untrusted / inaccessible / unknown policy.
    // The reason is bounded to the four permitted unavailable reasons; any other
    // value is coerced to Unknown so no permissive state can leak in.
    public static ManagedChefKeysGateProjection Unavailable(ManagedChefKeysGateReason reason)
    {
        ManagedChefKeysGateReason bounded = reason switch
        {
            ManagedChefKeysGateReason.PolicyInvalid => ManagedChefKeysGateReason.PolicyInvalid,
            ManagedChefKeysGateReason.PolicyUntrusted => ManagedChefKeysGateReason.PolicyUntrusted,
            ManagedChefKeysGateReason.PolicyUnavailable => ManagedChefKeysGateReason.PolicyUnavailable,
            _ => ManagedChefKeysGateReason.Unknown,
        };
        return new(ManagedChefKeysGateState.Unavailable, bounded, authorized: false);
    }

    // The single source of truth for the API `state` token. snake_case, bounded.
    public string WireState => State switch
    {
        ManagedChefKeysGateState.NotConfigured => "not_configured",
        ManagedChefKeysGateState.Disabled => "disabled",
        ManagedChefKeysGateState.AuthorizedNotProvisioned => "authorized_not_provisioned",
        _ => "unavailable",
    };

    // The single source of truth for the API `reason` token. snake_case, bounded.
    public string WireReason => Reason switch
    {
        ManagedChefKeysGateReason.NotConfigured => "not_configured",
        ManagedChefKeysGateReason.SelfService => "self_service",
        ManagedChefKeysGateReason.DisabledByPolicy => "disabled_by_policy",
        ManagedChefKeysGateReason.AuthorizedNotProvisioned => "authorized_not_provisioned",
        ManagedChefKeysGateReason.PolicyInvalid => "policy_invalid",
        ManagedChefKeysGateReason.PolicyUntrusted => "policy_untrusted",
        ManagedChefKeysGateReason.PolicyUnavailable => "policy_unavailable",
        _ => "unknown",
    };

    // Content-free: bounded state + reason tokens only. Never a registry path,
    // raw value, identifier, or secret.
    public override string ToString()
        => $"ManagedChefKeysGateProjection[state={WireState}, reason={WireReason}]";
}

// The pure, deterministic, side-effect-free authorization gate. It consumes the
// typed detection and never touches the registry, session provider, network,
// filesystem, a credential/key store, a service, a token, or any capability. It
// never throws through the boundary and maps a null/unknown detection to
// Unavailable/Unknown (fail closed).
public static class ManagedChefKeysGate
{
    // Maps the typed Cycle 3 detection onto the bounded gate projection.
    //
    // Authorization requires ALL THREE predicates simultaneously
    // (State == Configured AND DesktopAccess == OrganizationManaged AND
    // ManagedChefKeys == Enabled). Each predicate is checked explicitly as
    // defense in depth so no single-field slip can grant authority. Everything
    // else — null, self-service, disabled, invalid, untrusted, and any
    // unreachable/unsupported combination — fails closed. `enabled` here means
    // only that a FUTURE inventory is policy-authorized; it never activates,
    // provisions, or makes any key available (InventoryLoaded stays false).
    public static ManagedChefKeysGateProjection Evaluate(MachinePolicyDetection? detection)
    {
        if (detection is null)
        {
            return ManagedChefKeysGateProjection.Unavailable(ManagedChefKeysGateReason.Unknown);
        }

        switch (detection.State)
        {
            case MachinePolicyState.NotConfigured:
                // Absent / benign default: personal self-service, denied.
                return ManagedChefKeysGateProjection.NotConfiguredAbsent();

            case MachinePolicyState.Configured:
                if (detection.DesktopAccess == MachinePolicyDesktopAccess.SelfService)
                {
                    // Valid self-service policy: denied, never authorized.
                    return ManagedChefKeysGateProjection.NotConfiguredSelfService();
                }

                if (detection.DesktopAccess == MachinePolicyDesktopAccess.OrganizationManaged)
                {
                    // The single authorizing path: all three predicates hold.
                    if (detection.State == MachinePolicyState.Configured
                        && detection.DesktopAccess == MachinePolicyDesktopAccess.OrganizationManaged
                        && detection.ManagedChefKeys == MachinePolicyCapability.Enabled)
                    {
                        return ManagedChefKeysGateProjection.AuthorizedNotProvisioned();
                    }

                    if (detection.ManagedChefKeys == MachinePolicyCapability.Disabled)
                    {
                        // Organization-managed but managed keys turned off.
                        return ManagedChefKeysGateProjection.Disabled();
                    }
                }

                // Configured but desktopAccess is Undetermined / unsupported: this
                // is not producible from valid policy, so fail closed.
                return ManagedChefKeysGateProjection.Unavailable(ManagedChefKeysGateReason.Unknown);

            case MachinePolicyState.Invalid:
                // Inaccessible policy is distinct from malformed policy.
                return detection.RecoveryReason == MachinePolicyRecoveryReason.AccessDenied
                    ? ManagedChefKeysGateProjection.Unavailable(ManagedChefKeysGateReason.PolicyUnavailable)
                    : ManagedChefKeysGateProjection.Unavailable(ManagedChefKeysGateReason.PolicyInvalid);

            case MachinePolicyState.Untrusted:
                return ManagedChefKeysGateProjection.Unavailable(ManagedChefKeysGateReason.PolicyUntrusted);

            default:
                return ManagedChefKeysGateProjection.Unavailable(ManagedChefKeysGateReason.Unknown);
        }
    }
}
