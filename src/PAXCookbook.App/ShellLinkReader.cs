using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PAXCookbook.App;

// Read-only .lnk metadata via IShellLinkW (CLSID_ShellLink) — the WDAC-safe COM
// shell-link interface. Replaces the former WScript.Shell COM read in
// IconDiagnostics so the app uses NO Windows Script Host (strict corporate WDAC
// policies block script hosts). Best-effort: returns null on any failure.
internal static class ShellLinkReader
{
    public sealed record Info(string Target, string Arguments, string WorkingDirectory, string IconLocation);

    public static Info? Read(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;
        object? linkObj = null;
        try
        {
            linkObj = new ShellLink();
            var link = (IShellLinkW)linkObj;
            ((IPersistFile)linkObj).Load(lnkPath, STGM_READ);

            var sbTarget = new StringBuilder(1024);
            link.GetPath(sbTarget, sbTarget.Capacity, IntPtr.Zero, 0);
            var sbArgs = new StringBuilder(1024);
            link.GetArguments(sbArgs, sbArgs.Capacity);
            var sbWork = new StringBuilder(1024);
            link.GetWorkingDirectory(sbWork, sbWork.Capacity);
            var sbIcon = new StringBuilder(1024);
            link.GetIconLocation(sbIcon, sbIcon.Capacity, out var iconIndex);

            return new Info(
                Target: sbTarget.ToString(),
                Arguments: sbArgs.ToString(),
                WorkingDirectory: sbWork.ToString(),
                IconLocation: sbIcon.Length > 0 ? sbIcon + "," + iconIndex : "");
        }
        catch { return null; }
        finally
        {
            if (linkObj is not null)
            {
                try { Marshal.FinalReleaseComObject(linkObj); } catch { }
            }
        }
    }

    private const int STGM_READ = 0;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
