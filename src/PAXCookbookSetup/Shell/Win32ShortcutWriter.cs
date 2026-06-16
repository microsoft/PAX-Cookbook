using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PAXCookbookSetup.Shell;

// Real Windows shortcut writer. Uses IShellLinkW (CLSID_ShellLink) for
// the .lnk fields and IPropertyStore for AUMID +
// System.AppUserModel.ExcludeFromShowInNewInstall.
//
// Per Phase 8 contract:
//   - no raw .lnk byte editing
//   - AUMID always set to ProductConstants.Aumid via PKEY_AppUserModel_ID
//   - ExcludeFromShowInNewInstall attempted for maintenance shortcuts;
//     status is reported back in ShortcutWriteResult
public sealed class Win32ShortcutWriter : IShortcutWriter
{
    public ShortcutWriteResult Write(string folderPath, ShortcutDefinition def)
    {
        Directory.CreateDirectory(folderPath);
        var lnk = Path.Combine(folderPath, def.Name + ".lnk");
        if (File.Exists(lnk)) File.Delete(lnk);

        var shellLink = (IShellLinkW)new ShellLink();
        shellLink.SetPath(def.Target);
        shellLink.SetArguments(def.Arguments);
        if (!string.IsNullOrEmpty(def.WorkingDirectory))
            shellLink.SetWorkingDirectory(def.WorkingDirectory);
        if (!string.IsNullOrEmpty(def.IconLocation))
        {
            // IconLocation comes in as "path,index"
            var (p, i) = SplitIcon(def.IconLocation);
            shellLink.SetIconLocation(p, i);
        }

        bool excludeAttempted = false;
        bool excludeOk = false;

        var store = (IPropertyStore)shellLink;
        try
        {
            // PKEY_AppUserModel_ID
            using var aumid = PropVariantString.Wrap(def.Aumid);
            store.SetValue(ref PKEY_AppUserModel_ID, ref aumid.Variant);

            if (def.ExcludeFromRecommended)
            {
                excludeAttempted = true;
                using var excl = PropVariantBool.Wrap(true);
                store.SetValue(ref PKEY_AppUserModel_ExcludeFromShowInNewInstall, ref excl.Variant);
                excludeOk = true;
            }
            store.Commit();
        }
        catch
        {
            excludeOk = false;
        }

        var persist = (IPersistFile)shellLink;
        persist.Save(lnk, fRemember: true);

        var bytes = File.ReadAllBytes(lnk);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new ShortcutWriteResult(lnk, sha, excludeAttempted, excludeOk);
    }

    public void Delete(string lnkPath)
    {
        if (File.Exists(lnkPath)) File.Delete(lnkPath);
    }

    private static (string Path, int Index) SplitIcon(string spec)
    {
        var idx = spec.LastIndexOf(',');
        if (idx < 0) return (spec, 0);
        var p = spec.Substring(0, idx);
        var i = int.TryParse(spec.Substring(idx + 1), out var n) ? n : 0;
        return (p, i);
    }

    // ----- COM interop -----

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public PROPERTYKEY(Guid g, uint p) { fmtid = g; pid = p; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
        public int p2;
    }

    private static PROPERTYKEY PKEY_AppUserModel_ID =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    private static PROPERTYKEY PKEY_AppUserModel_ExcludeFromShowInNewInstall =
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 8);

    private const ushort VT_LPWSTR = 31;
    private const ushort VT_BOOL = 11;

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    private sealed class PropVariantString : IDisposable
    {
        public PROPVARIANT Variant;
        public static PropVariantString Wrap(string s)
        {
            var w = new PropVariantString();
            w.Variant.vt = VT_LPWSTR;
            w.Variant.p = Marshal.StringToCoTaskMemUni(s);
            return w;
        }
        public void Dispose() { PropVariantClear(ref Variant); }
    }

    private sealed class PropVariantBool : IDisposable
    {
        public PROPVARIANT Variant;
        public static PropVariantBool Wrap(bool b)
        {
            var w = new PropVariantBool();
            w.Variant.vt = VT_BOOL;
            w.Variant.p2 = b ? -1 : 0; // VARIANT_BOOL: -1 = true, 0 = false
            return w;
        }
        public void Dispose() { PropVariantClear(ref Variant); }
    }
}
