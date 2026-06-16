using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PAXCookbookSetup.Uninstall;

// Phase 10 — production ITaskbarLnkResolver.
//
// Reads .lnk metadata via the WScript.Shell COM automation object
// (CreateShortcut returns a WshShortcut whose TargetPath and
// WorkingDirectory properties are populated by the shell when the
// underlying .lnk file already exists). This avoids fragile binary
// parsing of the .lnk file format and avoids any IShellLink P/Invoke
// surface beyond what the Windows Script Host has guaranteed since
// Win2000. No new shortcut is created — CreateShortcut on an existing
// path returns its current state.
//
// AUMID extraction requires IPropertyStore on IShellLink which is
// deferred to a follow-on phase (a future IPinnedList3 probe). PAX
// taskbar pins always have Target / WorkingDirectory under the
// install root, so the positive-ID matcher will identify them via
// the target-under-installroot / workingdir-under-installroot rules
// without needing AUMID; the AUMID branch in PositiveIdReason is
// exercised by unit tests via the in-memory resolver.
//
// DeleteLnk uses File.Delete on the explicit .lnk path. We never
// enumerate or delete anything outside the taskbar pin folder we
// were constructed with — the safety boundary is enforced by
// LnkTaskbarPinCleaner (which only ever passes paths it received
// from EnumerateLnkFiles on the bound folder).
public sealed class ShellLinkResolver : ITaskbarLnkResolver
{
    public TaskbarLnkInfo? Resolve(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return null;
            var shell = Activator.CreateInstance(t);
            if (shell is null) return null;
            object? sc = t.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (sc is null) return null;
            var scType = sc.GetType();
            string target = (scType.InvokeMember("TargetPath",
                BindingFlags.GetProperty, null, sc, null) as string) ?? "";
            string wd = (scType.InvokeMember("WorkingDirectory",
                BindingFlags.GetProperty, null, sc, null) as string) ?? "";
            // AUMID is not exposed through WshShortcut. Leave empty;
            // PositiveIdReason handles AUMID-only matches via the
            // in-memory resolver path for tests, and PAX pins match by
            // target/workdir in practice.
            return new TaskbarLnkInfo(
                LnkPath: lnkPath,
                Target: target,
                WorkingDirectory: wd,
                Aumid: "");
        }
        catch
        {
            return null;
        }
    }

    public IEnumerable<string> EnumerateLnkFiles(string folderPath)
    {
        if (!Directory.Exists(folderPath)) yield break;
        // Top-level only. The taskbar pin folder is flat; we explicitly
        // refuse to recurse so an attacker who plants .lnk files in a
        // subfolder cannot trick us into touching them.
        string[] files;
        try { files = Directory.GetFiles(folderPath, "*.lnk", SearchOption.TopDirectoryOnly); }
        catch { yield break; }
        foreach (var f in files) yield return f;
    }

    public bool DeleteLnk(string lnkPath)
    {
        try
        {
            if (!File.Exists(lnkPath)) return true;
            File.SetAttributes(lnkPath, FileAttributes.Normal);
            File.Delete(lnkPath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
