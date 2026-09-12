namespace PAXCookbookSetup;

// Centralized customer-visible Setup UI copy. These strings are the ONLY
// installer surfaces a business user reads; they are kept here so the copy is
// unit-testable and provably carries NO customer-visible "experimental" word.
//
// The internal SetupChannel value "experimental" is UNCHANGED and is never a
// display string — it only gates whether the pre-release (test) chrome appears.
// The single authorized customer-visible "Experimental" notice is the running
// app shell's top-of-app banner (app/web), NOT Setup.
public static class SetupUiText
{
    // Setup window title — the bare product title on EVERY build (stable and
    // pre-release). No channel marker in the window chrome.
    public const string SetupWindowTitle = "PAX Cookbook Setup";

    // Retained thin safety strip shown only on pre-release (test) installers.
    public const string PreReleaseSafetyStrip = "Test build \u2014 not for production";

    // Leading sentence shown when a pre-release payload cannot be located during
    // an install/update. The caller appends the manual-download guidance.
    public const string NoPreReleaseBuildFound = "No pre-release build was found. ";
}
