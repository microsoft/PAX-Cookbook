namespace PAXCookbook.App;

// The SINGLE compile-capability authority for the experimental Entra WAM
// provider (Track 1 / T1-S2B). This is the ONLY place in the app that reads the
// EXPERIMENTAL_WAM compilation constant; every route, factory, and host-wiring
// decision consults this property instead of duplicating a #if. Because the real
// MSAL-backed authenticator (MsalEntraWamAuthenticator) exists ONLY under
// EXPERIMENTAL_WAM, a default/stable build reports false here and therefore can
// never activate the daemon endpoint or advertise capability — even if the
// experimental runtime environment variables happen to be set.
internal static class ExperimentalWamCapability
{
    // True only when the app was compiled with ExperimentalWam=true (the same
    // gate that adds the MSAL packages and compiles the real authenticator).
    internal static bool IsExperimentalAuthenticatorCompiled =>
#if EXPERIMENTAL_WAM
        true;
#else
        false;
#endif
}
