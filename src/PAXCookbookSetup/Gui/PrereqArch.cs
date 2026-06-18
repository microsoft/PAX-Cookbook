using System.Runtime.InteropServices;

namespace PAXCookbookSetup.Gui;

// Resolves the machine architecture for prerequisite download + detection.
//
// IMPORTANT: the Setup executable is published x64 and runs under x64 emulation
// on ARM64 Windows, so RuntimeInformation.ProcessArchitecture reports X64 there.
// The PAX Cookbook app, however, is launched through the NATIVE dotnet.exe host
// (C:\Program Files\dotnet — ARM64 on an ARM64 machine), so the runtime that
// must be installed is the one matching the OS architecture. Always resolve
// from OSArchitecture, never ProcessArchitecture, or an ARM64 machine receives
// the x64 runtime (which installs under Program Files (x86)\dotnet) and the
// native ARM64 host then reports "No frameworks were found".
public static class PrereqArch
{
    // The real machine architecture (NOT the emulated process architecture).
    public static Architecture Os => RuntimeInformation.OSArchitecture;

    public static bool IsArm64 => Os == Architecture.Arm64;

    // .NET RID architecture token ("arm64", "x86", "x64") for the OS arch.
    public static string Rid() => Rid(Os);

    public static string Rid(Architecture arch) => arch switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };
}
