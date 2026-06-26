using System.Runtime.InteropServices;

namespace PAXCookbook.Shared.Platform;

// Minimum-OS gate shared by the app (PAXCookbook.exe) and the Setup utility
// (PAXCookbookSetup.exe). PAX Cookbook requires Windows 10 or later, so both
// entry points stop early on older Windows with a friendly message instead of
// letting the user reach a broken state.
//
// Detection uses RtlGetVersion (ntdll), which reports the TRUE OS version and
// is NOT affected by the application-compatibility (GetVersionEx) manifest
// shimming that otherwise pins a caller without an explicit supportedOS
// manifest entry to 6.2/6.3 on Windows 8.1+. Environment.OSVersion is a
// secondary fallback (it also reports the real version on modern .NET). The
// gate fails OPEN -- it treats the OS as supported when the version cannot be
// determined at all -- so a real Windows 10/11 machine is never falsely
// blocked.
public static class WindowsVersionGate
{
    // Windows 10 AND Windows 11 both report major version 10 via RtlGetVersion
    // (Windows 11 is build >= 22000). Windows 8.1 = 6.3, Windows 8 = 6.2,
    // Windows 7 = 6.1. A major-version floor of 10 is the exact "Windows 10 or
    // later" predicate.
    public const int MinimumSupportedMajorVersion = 10;

    public const string ProductName = "PAX Cookbook";

    // Friendly, explanation-free message (no mention of the underlying OS APIs).
    public const string UnsupportedMessage =
        "PAX Cookbook requires Windows 10 or later. This computer is running an "
        + "earlier version of Windows, so the app can't run here.";

    [StructLayout(LayoutKind.Sequential)]
    private struct RtlOsVersionInfoEx
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
    }

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref RtlOsVersionInfoEx versionInfo);

    // Returns the true major OS version, or null when it cannot be determined.
    public static int? TryGetTrueMajorVersion()
    {
        try
        {
            var info = new RtlOsVersionInfoEx
            {
                dwOSVersionInfoSize = (uint)Marshal.SizeOf<RtlOsVersionInfoEx>(),
            };
            // RtlGetVersion returns STATUS_SUCCESS (0) and succeeds on any
            // Windows that has ntdll (i.e. every version of Windows).
            if (RtlGetVersion(ref info) == 0)
            {
                return (int)info.dwMajorVersion;
            }
        }
        catch
        {
            // P/Invoke unavailable (missing ntdll / non-Windows) -- fall through
            // to the managed fallback below.
        }

        try
        {
            return Environment.OSVersion.Version.Major;
        }
        catch
        {
            return null;
        }
    }

    // Pure decision over a (possibly unknown) major version. Exposed for tests:
    // 6 (Windows 7/8/8.1) -> false, 10/11 -> true, null (undetectable) -> true.
    public static bool IsSupportedMajorVersion(int? majorVersion)
        => !majorVersion.HasValue
           || majorVersion.Value >= MinimumSupportedMajorVersion;

    // True when the OS is Windows 10 or later, or when the version cannot be
    // determined at all (fail open so a supported machine is never blocked).
    public static bool IsSupported() => IsSupportedMajorVersion(TryGetTrueMajorVersion());
}
