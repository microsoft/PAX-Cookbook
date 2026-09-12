using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SectionB.PositiveFixture;

public static class PositiveFixture
{
    public static void NeverInvokeProcessCreation()
    {
        var startInfo = new ProcessStartInfo("section-b-calibration-never-run.exe");
        _ = Process.Start(startInfo);
    }

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteW", CharSet = CharSet.Unicode)]
    private static extern nint ShellExecute(
        nint window,
        string operation,
        string file,
        string parameters,
        string directory,
        int showCommand);

    [DllImport("shell32.dll", EntryPoint = "ShellExecuteExW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo executeInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public nint Window;
        public nint Verb;
        public nint File;
        public nint Parameters;
        public nint Directory;
        public int Show;
        public nint Instance;
    }
}