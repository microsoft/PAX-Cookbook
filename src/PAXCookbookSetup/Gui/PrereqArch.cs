using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PAXCookbookSetup.Gui;

// Resolves the REAL machine architecture for prerequisite download + detection.
//
// The Setup executable is published win-x64 and runs under x64 EMULATION on
// ARM64 Windows. In that emulated process RuntimeInformation.OSArchitecture
// returns X64 (the emulator presents an x64 "native system" view) and the
// per-process %PROCESSOR_ARCHITECTURE% env var reads "AMD64" — so both would
// drive the x64 runtime onto an ARM64 machine, where the native ARM64
// dotnet.exe host then reports "No frameworks were found". (This is exactly the
// bug that shipped in 59fbb78, which relied on OSArchitecture.)
//
// The machine PROCESSOR_ARCHITECTURE under
//   HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment
// is the ground truth: it records the real hardware ("ARM64"/"AMD64"/"x86") and
// is NOT rewritten by emulation. We read that first and fall back to
// OSArchitecture only when the registry read fails or is unrecognised.
public static class PrereqArch
{
    private const string MachineEnvKey =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    private static readonly Architecture _os;
    private static readonly string _method;

    static PrereqArch()
    {
        _os = Resolve(out _method);
    }

    // The real machine architecture (NOT the emulated process architecture).
    public static Architecture Os => _os;

    // How Os was resolved, for diagnostics: e.g. "registry:ARM64",
    // "registry:AMD64", or "osarchitecture:X64" when the registry read failed.
    public static string ResolutionMethod => _method;

    public static bool IsArm64 => _os == Architecture.Arm64;

    // .NET RID architecture token ("arm64", "x86", "x64") for the OS arch.
    public static string Rid() => Rid(_os);

    public static string Rid(Architecture arch) => arch switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    // Maps a machine PROCESSOR_ARCHITECTURE value to an Architecture, or null
    // when absent/unrecognised. Pure (unit-tested). Windows reports "AMD64",
    // "ARM64", or "x86" (case-insensitively).
    public static Architecture? MapProcessorArchitecture(string? value)
        => value?.Trim().ToUpperInvariant() switch
        {
            "ARM64" => Architecture.Arm64,
            "AMD64" => Architecture.X64,
            "X86" => Architecture.X86,
            _ => null,
        };

    private static Architecture Resolve(out string method)
    {
        // PRIMARY: the machine registry PROCESSOR_ARCHITECTURE — the real
        // hardware, unaffected by x64 emulation. HKLM\SYSTEM\... is a system key
        // (not under WOW6432Node), so an x64-emulated process reads the same
        // value the native OS wrote ("ARM64" on an ARM64 machine).
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MachineEnvKey);
            var raw = key?.GetValue("PROCESSOR_ARCHITECTURE") as string;
            if (MapProcessorArchitecture(raw) is Architecture mapped)
            {
                method = "registry:" + raw;
                return mapped;
            }
        }
        catch
        {
            // Fall through to the runtime fallback.
        }

        // FALLBACK: OSArchitecture. Correct on native x64/x86; only unreliable
        // under x64 emulation on ARM64 — and reached only if the registry read
        // failed, which is itself unusual.
        var os = RuntimeInformation.OSArchitecture;
        method = "osarchitecture:" + os;
        return os;
    }
}
