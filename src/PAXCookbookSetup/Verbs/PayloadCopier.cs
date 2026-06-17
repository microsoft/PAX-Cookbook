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
        // SetupExe → installRoot\Setup\<name>, ONLY when the build embedded it in
        // the payload. In the bootstrapper model the Setup EXE is NOT shipped in
        // the payload (it is downloaded separately for repair/update), so skip it.
        var setupSrc = SafePath.CombineUnderRoot(payloadRoot, m.Payload.SetupExe.Name);
        if (File.Exists(setupSrc))
        {
            var setupDst = SafePath.CombineUnderRoot(installRoot,
                Path.Combine("Setup", m.Payload.SetupExe.Name));
            Directory.CreateDirectory(Path.GetDirectoryName(setupDst)!);
            File.Copy(setupSrc, setupDst, overwrite: true);
        }
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
        // The installed Setup EXE is NOT verified here: in the bootstrapper model
        // it is the self-installed Setup runtime (a different build than the
        // manifest's setupExe metadata, which describes the downloadable Setup),
        // not a payload artifact. The App EXE + payload files are the integrity
        // surface for an install.
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
