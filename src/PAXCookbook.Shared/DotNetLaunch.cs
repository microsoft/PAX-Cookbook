using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PAXCookbook.Shared;

// Resolves the launch surface for PAX Cookbook under corporate WDAC, which
// blocks unsigned executables. The app is framework-dependent, so every launch
// must run the Microsoft-signed dotnet.exe host with the app DLL as its
// argument. Launching the unsigned apphost ("PAX Cookbook.exe") directly is
// blocked; the EXE is kept only as the shortcut icon source (reading an icon is
// allowed, executing is not).
public static class DotNetLaunch
{
    // Full path to the Microsoft-signed dotnet.exe host. Prefers the standard
    // per-machine install locations; falls back to the bare "dotnet.exe" name
    // (resolved via the App Paths key / PATH at launch time) when the
    // well-known path is absent. .NET 8 is a Setup prerequisite, so the
    // well-known path normally exists at registration time.
    public static string DotNetExePath()
    {
        foreach (var env in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)" })
        {
            string? pf;
            try { pf = Environment.GetEnvironmentVariable(env); }
            catch { pf = null; }
            if (string.IsNullOrEmpty(pf)) continue;
            var candidate = Path.Combine(pf, "dotnet", "dotnet.exe");
            try { if (File.Exists(candidate)) return candidate; }
            catch { /* ignore and keep probing */ }
        }
        return "dotnet.exe";
    }

    // <installRoot>\App\bin\PAX Cookbook.dll — the managed entry assembly that
    // dotnet.exe runs. This is the launch target.
    public static string AppDllPath(string installRoot)
        => Path.Combine(installRoot, ProductConstants.AppRootFolderName,
                        ProductConstants.BinRootFolderName, ProductConstants.AppDllName);

    // <installRoot>\App\bin\PAX Cookbook.exe — the apphost. NEVER executed under
    // WDAC; used only as the icon source for shortcuts.
    public static string AppExePath(string installRoot)
        => Path.Combine(installRoot, ProductConstants.AppRootFolderName,
                        ProductConstants.BinRootFolderName, ProductConstants.AppExeName);

    // True when ANY whitespace-delimited, quote-aware token of a command line is
    // a filesystem path that resolves under installRoot. Used for positive-ID of
    // OUR registry commands / shortcuts before removal: with the dotnet launch
    // model the first token is the shared signed dotnet.exe (OUTSIDE the install
    // root), so the install-specific path is now an argument (the app DLL /
    // --workspace / --approot). A command whose only in-root reference is gone
    // belongs to a different installation and is left alone.
    public static bool CommandReferencesInstallRoot(string? command, string installRoot)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        string rootFull;
        try
        {
            rootFull = Path.GetFullPath(installRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        }
        catch { return false; }

        foreach (var token in Tokenize(command!))
        {
            if (string.IsNullOrWhiteSpace(token)) continue;
            string full;
            try { full = Path.GetFullPath(token); }
            catch { continue; }
            if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Splits a command line into tokens, treating double-quoted spans as single
    // tokens (quotes themselves are stripped). Good enough for the simple,
    // self-generated commands we positive-ID (no escaped quotes).
    private static IEnumerable<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (var c in command)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ' ' && !inQuotes)
            {
                if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }
}
