using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.Shared.Hashing;
using PAXCookbook.Shared.Io;

namespace PAXCookbookSetup.Verbs;

// Shared payload copy helper.
public static class PayloadCopier
{
    public static void Copy(Manifest m, string payloadRoot, string installRoot, string appRoot)
    {
        // Non-EXE files first.
        foreach (var f in m.Payload.Files)
        {
            var src = SafePath.CombineUnderRoot(payloadRoot, f.RelativeInstallPath);
            var dst = SafePath.CombineUnderRoot(installRoot, f.RelativeInstallPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true);
        }
        // SetupExe → installRoot\Setup\<name>
        var setupSrc = SafePath.CombineUnderRoot(payloadRoot, m.Payload.SetupExe.Name);
        var setupDst = SafePath.CombineUnderRoot(installRoot,
            Path.Combine("Setup", m.Payload.SetupExe.Name));
        Directory.CreateDirectory(Path.GetDirectoryName(setupDst)!);
        File.Copy(setupSrc, setupDst, overwrite: true);
        // AppExe last.
        var appSrc = SafePath.CombineUnderRoot(payloadRoot, m.Payload.AppExe.RelativeInstallPath);
        var appDst = SafePath.CombineUnderRoot(installRoot, m.Payload.AppExe.RelativeInstallPath);
        Directory.CreateDirectory(Path.GetDirectoryName(appDst)!);
        File.Copy(appSrc, appDst, overwrite: true);
    }

    public static void VerifyInstalled(Manifest m, string installRoot)
    {
        VerifyOne(installRoot, m.Payload.AppExe.RelativeInstallPath,
                  m.Payload.AppExe.Sha256, m.Payload.AppExe.SizeBytes);
        VerifyOne(installRoot, Path.Combine("Setup", m.Payload.SetupExe.Name),
                  m.Payload.SetupExe.Sha256, m.Payload.SetupExe.SizeBytes);
        foreach (var f in m.Payload.Files)
            VerifyOne(installRoot, f.RelativeInstallPath, f.Sha256, f.SizeBytes);
    }

    private static void VerifyOne(string installRoot, string rel, string sha, long size)
    {
        var p = SafePath.CombineUnderRoot(installRoot, rel);
        var fi = new FileInfo(p);
        if (!fi.Exists) throw new IOException($"verify: file missing {p}");
        if (fi.Length != size) throw new IOException($"verify: size mismatch {p}");
        var actual = Sha256Hash.OfFile(p);
        if (!Sha256Hash.Equal(actual, sha))
            throw new IOException($"verify: sha mismatch {p}");
    }
}
