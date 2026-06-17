using System.Runtime.InteropServices;

namespace PAXCookbookSetup;

// Removes the "Mark of the Web" (the Zone.Identifier alternate data stream)
// that Windows attaches to files originating from the internet. The Setup EXE
// and the payload zip are downloaded from a GitHub Release, so the
// self-installed Setup copy and the installed application files can inherit
// MOTW. Enterprise security policy (SmartScreen / WDAC / AppLocker) then blocks
// the unsigned, internet-sourced PAX Cookbook.exe on launch with
// "Your administrator caused Windows Security to block this action".
//
// Stripping the Zone.Identifier stream targets only the alternate data stream
// (the "<path>:Zone.Identifier" name); the file's primary content stream is
// untouched, so SHA-256 verification of the file is unaffected. All operations
// are best-effort and never throw.
public static class MarkOfTheWeb
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode,
               EntryPoint = "DeleteFileW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteFileNative(string lpFileName);

    // Strip the Zone.Identifier stream from a single file. Returns true when the
    // stream was deleted; false when the file/stream did not exist or the call
    // failed (both are normal — most files carry no MOTW). Never throws.
    public static bool StripFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;
        try { return DeleteFileNative(filePath + ":Zone.Identifier"); }
        catch { return false; }
    }

    // Strip the Zone.Identifier stream from every file under a directory tree.
    // Returns the number of files visited (not the number that carried a
    // stream). Never throws — enumeration and per-file failures are swallowed
    // so a hardened/locked file cannot abort the install.
    public static int StripTree(string root)
    {
        if (string.IsNullOrEmpty(root)) return 0;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
        catch { return 0; }

        int visited = 0;
        foreach (var f in files)
        {
            StripFile(f);
            visited++;
        }
        return visited;
    }
}
