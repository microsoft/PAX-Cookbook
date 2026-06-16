namespace PAXCookbook.Cli;

// Minimal argv parser for PAXCookbook.exe.
//
// Verb is args[0]. The only supported flag is --install-root <path>, which
// is accepted for direct command-mode only (dev/test) and is rejected by
// the protocol command path.
public sealed record AppArgs(
    string Verb,
    string? InstallRoot,
    IReadOnlyList<string> Positional);

public static class AppArgsParser
{
    public static AppArgs Parse(string[] args)
    {
        if (args is null || args.Length == 0)
            return new AppArgs("help", null, Array.Empty<string>());

        var verb = args[0].ToLowerInvariant();
        string? installRoot = null;
        var positional = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "--install-root", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException("--install-root requires a path");
                installRoot = args[++i];
            }
            else
            {
                positional.Add(a);
            }
        }
        return new AppArgs(verb, installRoot, positional);
    }
}
