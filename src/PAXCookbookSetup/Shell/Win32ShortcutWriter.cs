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
        // FIX 4 / WDAC belt-and-suspenders: create the shortcut MINIMIZED
        // (SW_SHOWMINNOACTIVE = 7). The shortcut now targets the console-subsystem
        // dotnet.exe host directly, so starting minimized means that even before
        // the app hides its own console window at startup, the host window is
        // never shown — the user never sees any console.
        shellLink.SetShowCmd(SW_SHOWMINNOACTIVE);
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

        // The .lnk is now fully written and flushed: IPersistFile.Save commits and
        // closes the file synchronously, and the File.ReadAllBytes above proves it
        // is complete and unlocked on disk. Release the shell-link COM object, then
        // notify the shell that a new item exists so Explorer and the Start Menu
        // app list re-index it immediately instead of waiting for their own
        // folder-watch to notice a programmatically written .lnk. SHCNE_CREATE
        // targets the .lnk; SHCNE_UPDATEDIR refreshes the parent folder for the
        // Start Menu cache. Advisory only — a notification failure must never fail
        // the install, since the shortcut is already written and verified above.
        try
        {
            Marshal.FinalReleaseComObject(shellLink);
            SHChangeNotify(SHCNE_CREATE, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, lnk, IntPtr.Zero);
            SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, folderPath, IntPtr.Zero);
        }
        catch { /* shell notification is best-effort; the .lnk is already on disk */ }

        return new ShortcutWriteResult(lnk, sha, excludeAttempted, excludeOk);
    }

    public void Delete(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return;
        File.Delete(lnkPath);

        // Symmetric with Write: notify the shell the item is gone so Explorer and
        // the Start Menu app list drop it promptly rather than showing a stale
        // entry until their own folder-watch catches up. Advisory only.
        try
        {
            SHChangeNotify(SHCNE_DELETE, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, lnkPath, IntPtr.Zero);
            var parent = Path.GetDirectoryName(lnkPath);
            if (!string.IsNullOrEmpty(parent))
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, parent, IntPtr.Zero);
        }
        catch { /* shell notification is best-effort */ }
    }

    // Read-only .lnk metadata via IShellLinkW (the same WDAC-safe COM path used
    // for writing; NO Windows Script Host). Returns null when the file is missing
    // or cannot be read. AUMID is not read here — callers that need it use
    // IPropertyStore separately. This replaces the former WScript.Shell COM read.
    public static ShortcutReadResult? ReadLink(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;
        try
        {
            var shellLink = (IShellLinkW)new ShellLink();
            ((IPersistFile)shellLink).Load(lnkPath, STGM_READ);

            var sbTarget = new System.Text.StringBuilder(1024);
            shellLink.GetPath(sbTarget, sbTarget.Capacity, IntPtr.Zero, 0);
            var sbArgs = new System.Text.StringBuilder(1024);
            shellLink.GetArguments(sbArgs, sbArgs.Capacity);
            var sbWork = new System.Text.StringBuilder(1024);
            shellLink.GetWorkingDirectory(sbWork, sbWork.Capacity);
            var sbIcon = new System.Text.StringBuilder(1024);
            shellLink.GetIconLocation(sbIcon, sbIcon.Capacity, out var iconIndex);

            return new ShortcutReadResult(
                Target: sbTarget.ToString(),
                Arguments: sbArgs.ToString(),
                WorkingDirectory: sbWork.ToString(),
                IconLocation: sbIcon.Length > 0 ? sbIcon + "," + iconIndex : "");
        }
        catch { return null; }
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

    // SW_SHOWMINNOACTIVE — the .lnk ShowCmd that starts the target minimized and
    // without activating it. STGM_READ — IPersistFile.Load read mode.
    private const int SW_SHOWMINNOACTIVE = 7;
    private const int STGM_READ = 0;

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    // Shell change notification — tells Explorer / the Start Menu that a shortcut
    // was created or removed so its cached app list updates immediately, rather
    // than relying on the shell's own best-effort folder-watch (which can miss a
    // programmatically written .lnk, leaving it absent from the Start Menu app
    // list even though it exists on disk). SHChangeNotify has no A/W variants; the
    // SHCNF_PATHW flag selects the wide-string form of dwItem1/dwItem2.
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, [MarshalAs(UnmanagedType.LPWStr)] string dwItem1, IntPtr dwItem2);

    // SHCNE_* event IDs and SHCNF_* flags for SHChangeNotify. For SHCNE_CREATE,
    // SHCNE_DELETE and SHCNE_UPDATEDIR only dwItem1 is used (dwItem2 is null).
    // SHCNF_PATHW marks dwItem1 as a wide-string path; SHCNF_FLUSHNOWAIT forces
    // the notification to be dispatched immediately without blocking the caller.
    private const int SHCNE_CREATE = 0x00000002;
    private const int SHCNE_DELETE = 0x00000004;
    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNF_PATHW = 0x0005;
    private const uint SHCNF_FLUSHNOWAIT = 0x2000;

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
