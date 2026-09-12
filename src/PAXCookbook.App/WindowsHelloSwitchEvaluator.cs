namespace PAXCookbook.App;

// Independent integrity classification of the LOCAL PAX Windows Hello
// registration. This is NOT a device/platform availability signal — it only
// describes the local credential store, and it is one of the TWO SEPARATE
// predicates that decide a Work-account -> Windows Hello switch.
internal enum HelloLocalRegistration
{
    // No local PAX Hello credential exists yet (enrollment has never completed).
    // A first-run switch must ENROLL before it can select Windows Hello.
    NotRegistered = 0,

    // Exactly the expected, structurally-valid ES256 credential material exists.
    // A switch to Windows Hello can be selected atomically without re-enrolling.
    Valid = 1,

    // A local credential record exists but is malformed/unusable (unreadable
    // file, corrupt JSON, wrong schema, or a credential missing required
    // material / using an unsupported algorithm). This must NOT be silently
    // treated as "not registered" and must NOT be silently re-enrolled — the
    // switch fails closed toward Setup Repair.
    Malformed = 2,
}

// The bounded outcome of a Work-account -> Windows Hello switch request. Exactly
// one is produced from the TWO SEPARATE predicates (platform availability and
// local registration integrity). No automatic fallback is ever implied: a
// non-switch decision leaves the currently selected provider untouched.
internal enum WindowsHelloSwitchDecision
{
    // (a) Hello available + valid local registration -> perform the atomic
    //     Select(WindowsHello). This is the ONLY decision that persists.
    SwitchReady = 0,

    // (b) Hello available + not registered -> enroll FIRST (reuse the WebAuthn
    //     registration ceremony); the provider stays unchanged until enrollment
    //     succeeds and a subsequent request resolves to SwitchReady.
    EnrollmentRequired = 1,

    // (c) Hello genuinely unavailable at the platform level -> do NOT enroll and
    //     do NOT switch; report a truthful bounded "not available right now".
    PlatformUnavailable = 2,

    // (d) Local registration malformed -> fail closed; do NOT switch and do NOT
    //     silently re-enroll; route toward Setup Repair.
    RegistrationRepairRequired = 3,
}

// Pure decision for the Work-account -> Windows Hello provider switch. Keeps the
// two predicates strictly separate: platform availability (the browser
// isUserVerifyingPlatformAuthenticatorAvailable() signal — NOT the local
// credential count) and local PAX registration integrity. Platform availability
// is evaluated FIRST: a genuinely unavailable platform can never enroll, so it
// is reported truthfully even when a stale/malformed local record is present.
internal static class WindowsHelloSwitchEvaluator
{
    internal static WindowsHelloSwitchDecision Evaluate(
        bool platformAvailable,
        HelloLocalRegistration registration)
    {
        if (!platformAvailable)
        {
            // (c) No enrollment is attempted on an unproven platform.
            return WindowsHelloSwitchDecision.PlatformUnavailable;
        }

        return registration switch
        {
            HelloLocalRegistration.Valid => WindowsHelloSwitchDecision.SwitchReady,           // (a)
            HelloLocalRegistration.NotRegistered => WindowsHelloSwitchDecision.EnrollmentRequired, // (b)
            HelloLocalRegistration.Malformed => WindowsHelloSwitchDecision.RegistrationRepairRequired, // (d)
            // Any unknown enum value fails closed toward repair rather than switching.
            _ => WindowsHelloSwitchDecision.RegistrationRepairRequired,
        };
    }

    // The explicit, distinct, bounded reason surfaced to the renderer for a
    // non-switch decision. SwitchReady has no reason (the switch is performed).
    // These identifiers replace the overloaded/stale "windows_hello_unavailable".
    internal static string ReasonFor(WindowsHelloSwitchDecision decision) => decision switch
    {
        WindowsHelloSwitchDecision.EnrollmentRequired => "windows_hello_enrollment_required",
        WindowsHelloSwitchDecision.PlatformUnavailable => "windows_hello_platform_unavailable",
        WindowsHelloSwitchDecision.RegistrationRepairRequired => "windows_hello_registration_repair_required",
        _ => "windows_hello_switch_failed",
    };
}
