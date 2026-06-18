using System.Collections.Generic;
using System.IO;
using PAXCookbookSetup.Shell;

namespace PAXCookbookSetup.Uninstall;

// Phase 10 — production ITaskbarLnkResolver.
//
// Reads .lnk metadata (Target + WorkingDirectory) via IShellLinkW
// (Win32ShortcutWriter.ReadLink) — the same WDAC-safe COM shell-link interface
// used to CREATE the shortcuts. NO Windows Script Host is involved (strict
// corporate WDAC blocks script hosts), and no fragile binary .lnk parsing.
//
// AUMID is not read here. PAX taskbar pins always have Target / WorkingDirectory
// under the install root, so the positive-ID matcher identifies them via the
// target-under-installroot / workingdir-under-installroot rules without needing
// AUMID; the AUMID branch in PositiveIdReason is exercised by unit tests via the
// in-memory resolver.
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
        var read = Win32ShortcutWriter.ReadLink(lnkPath);
        if (read is null) return null;
        // AUMID is not read here. PositiveIdReason handles AUMID-only matches via
        // the in-memory resolver path for tests; PAX pins match by target/workdir
        // in practice.
        return new TaskbarLnkInfo(
            LnkPath: lnkPath,
            Target: read.Target,
            WorkingDirectory: read.WorkingDirectory,
            Aumid: "");
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
