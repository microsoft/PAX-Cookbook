// PAX Cookbook - SERVICE ADMIN HELPER SIGNING POLICY (cycle 61)
//
// SECURITY CLASSIFICATION - READ THIS FIRST. This file is STRUCTURE ONLY. It
// performs NO live signature verification, opens NO certificate store, reads NO
// certificate chain, contacts NO revocation endpoint, and invokes NO elevation.
// It exists so the eventual GA policy has a fail-closed shape to grow into.
//
// The helper this policy describes is UNSIGNED PRERELEASE CODE. An unsigned
// helper does not resist replacement by a local user; UAC may display an
// unknown publisher; prerelease validation proves functionality, not production
// tamper resistance. This is unacceptable for GA. GA activation remains BLOCKED
// until the exact helper is Authenticode signed and the expected publisher
// policy is configured and verified (cycle-60 ruling).
//
// THE OPT-IN IS COMPILE TIME AND NOWHERE ELSE. The prerelease unsigned
// allowance is gated on the PAXCOOKBOOK_PRERELEASE_UNSIGNED_HELPER constant,
// which is defined ONLY by /p:PrereleaseUnsignedHelper=true (the existing
// TestIsolation / ManagedInventoryProvisioning precedent). There is
// deliberately NO environment variable, NO command-line argument, NO registry
// value and NO configuration file that can grant it. A normal or default build
// therefore never silently allows unsigned elevation.
//
// ABSENT PUBLISHER FAILS CLOSED. When GA policy is in force and no expected
// publisher is configured, the answer is SigningPolicyUnavailable - never a
// permissive one. A self-signed development certificate is never accepted as GA
// evidence.
using System;

namespace PAXCookbook.ServiceAdminHelper.Signing;

/// <summary>
/// Bounded signing-policy states. Zero is the permanent, safe default so an
/// uninitialised value can never read as an allowance.
/// </summary>
internal enum ServiceHelperSigningPolicyState
{
    Unspecified = 0,

    /// <summary>
    /// Internal prerelease development and attended validation only. Granted
    /// ONLY by the compile-time opt-in. Asserts nothing about trust.
    /// </summary>
    UnsignedPrereleaseAllowed = 1,

    /// <summary>
    /// GA policy is in force with a configured expected publisher, and the
    /// requirement is NOT yet satisfied.
    /// </summary>
    TrustedPublisherRequired = 2,

    /// <summary>
    /// GA policy is in force but no expected publisher is configured. This is a
    /// REFUSAL, never a pass-through.
    /// </summary>
    SigningPolicyUnavailable = 3,

    /// <summary>The observed signature is not acceptable under the policy in force.</summary>
    SignatureRejected = 4,

    /// <summary>
    /// GA policy is in force and the observed signature matched the configured
    /// expected publisher.
    /// </summary>
    TrustedPublisherSatisfied = 5,
}

/// <summary>
/// A bounded description of what a future live verifier could observe. Supplied
/// by a caller in this slice; nothing here derives it, because nothing here
/// performs verification.
/// </summary>
internal enum ServiceHelperSignatureObservation
{
    Unspecified = 0,
    Absent = 1,
    Invalid = 2,
    UntrustedChain = 3,
    Revoked = 4,
    ExpiredWithoutValidTimestamp = 5,
    SelfSignedDevelopmentCertificate = 6,
    ValidWithPublisher = 7,
}

/// <summary>
/// The fail-closed signing policy. Every member is a pure decision function.
/// </summary>
internal static class ServiceHelperSigningPolicy
{
    /// <summary>
    /// The configured expected publisher for GA. It is a compile-time constant
    /// and is EMPTY because no approved Authenticode signing identity has been
    /// supplied yet. It can never be set from the environment, the command line,
    /// the registry or a configuration file.
    /// </summary>
    internal const string ConfiguredExpectedPublisher = "";

    /// <summary>
    /// True only in a build that explicitly opted in with
    /// /p:PrereleaseUnsignedHelper=true.
    /// </summary>
    internal static bool PrereleaseUnsignedOptInCompiledIn =>
#if PAXCOOKBOOK_PRERELEASE_UNSIGNED_HELPER
        true;
#else
        false;
#endif

    /// <summary>True only when GA has a configured expected publisher.</summary>
    internal static bool ExpectedPublisherConfigured =>
        !string.IsNullOrWhiteSpace(ConfiguredExpectedPublisher);

    /// <summary>
    /// The policy in force, from compile-time facts only. Takes no argument, so
    /// no caller can influence it.
    /// </summary>
    internal static ServiceHelperSigningPolicyState ResolveConfiguredPolicy()
    {
        if (PrereleaseUnsignedOptInCompiledIn)
        {
            return ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed;
        }

        return ExpectedPublisherConfigured
            ? ServiceHelperSigningPolicyState.TrustedPublisherRequired
            : ServiceHelperSigningPolicyState.SigningPolicyUnavailable;
    }

    /// <summary>
    /// Map a bounded observation onto the policy in force. PURE: no I/O, no
    /// certificate access, no elevation. Nothing in this slice calls it with a
    /// real observation because nothing in this slice observes one.
    /// </summary>
    internal static ServiceHelperSigningPolicyState EvaluateObservation(
        ServiceHelperSignatureObservation observation, string? observedPublisher)
    {
        ServiceHelperSigningPolicyState policy = ResolveConfiguredPolicy();

        // Fail closed FIRST. An unconfigured GA policy is never permissive, no
        // matter how good the observation looks.
        if (policy == ServiceHelperSigningPolicyState.SigningPolicyUnavailable)
        {
            return ServiceHelperSigningPolicyState.SigningPolicyUnavailable;
        }

        // A development certificate is never evidence, under any policy.
        if (observation == ServiceHelperSignatureObservation.SelfSignedDevelopmentCertificate)
        {
            return ServiceHelperSigningPolicyState.SignatureRejected;
        }

        if (policy == ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed)
        {
            // Prerelease tolerates an absent signature and does not attempt to
            // derive trust from a present one. Anything actively broken is still
            // rejected.
            return observation is ServiceHelperSignatureObservation.Absent
                or ServiceHelperSignatureObservation.ValidWithPublisher
                ? ServiceHelperSigningPolicyState.UnsignedPrereleaseAllowed
                : ServiceHelperSigningPolicyState.SignatureRejected;
        }

        if (observation != ServiceHelperSignatureObservation.ValidWithPublisher)
        {
            return ServiceHelperSigningPolicyState.SignatureRejected;
        }

        return string.Equals(observedPublisher, ConfiguredExpectedPublisher, StringComparison.Ordinal)
            ? ServiceHelperSigningPolicyState.TrustedPublisherSatisfied
            : ServiceHelperSigningPolicyState.SignatureRejected;
    }
}
