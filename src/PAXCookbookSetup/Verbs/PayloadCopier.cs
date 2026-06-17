using System.Threading;
using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.Shared.Hashing;
using PAXCookbook.Shared.Io;

namespace PAXCookbookSetup.Verbs;

// Shared payload copy helper.
public static class PayloadCopier
{
    // A file that has just been released by a stopped process can stay briefly
    // locked while the OS flushes handles, so each copy is retried a few times
    // before giving up. On final failure the destination path is named so the
    // installer can tell the user exactly which file is in use.
    private const int MaxCopyAttempts = 6;
    private const int CopyRetryDelayMs = 400;

    public static void Copy(Manifest m, string payloadRoot, string installRoot, string appRoot)
    {
        // Non-EXE files first.
        foreach (var f in m.Payload.Files)
        {
            var src = SafePath.CombineUnderRoot(payloadRoot, f.RelativeInstallPath);
            var dst = SafePath.CombineUnderRoot(installRoot, f.RelativeInstallPath);
            CopyFileWithRetry(src, dst);
        }
        // SetupExe → installRoot\Setup\<name>, ONLY when the build embedded it in
        // the payload. In the bootstrapper model the Setup EXE is NOT shipped in
        // the payload (it is downloaded separately for repair/update), so skip it.
        var setupSrc = SafePath.CombineUnderRoot(payloadRoot, m.Payload.SetupExe.Name);
        if (File.Exists(setupSrc))
        {
            var setupDst = SafePath.CombineUnderRoot(installRoot,
                Path.Combine("Setup", m.Payload.SetupExe.Name));
            CopyFileWithRetry(setupSrc, setupDst);
        }
        // AppExe last.
        var appSrc = SafePath.CombineUnderRoot(payloadRoot, m.Payload.AppExe.RelativeInstallPath);
        var appDst = SafePath.CombineUnderRoot(installRoot, m.Payload.AppExe.RelativeInstallPath);
        CopyFileWithRetry(appSrc, appDst);
    }

    // Copy one file, overwriting an existing destination. When the destination
    // is momentarily locked (a just-stopped app still releasing the handle) the
    // copy is retried; a file that stays locked is reported by name so the
    // installer surfaces a clear "file in use" message instead of a bare error.
    private static void CopyFileWithRetry(string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(src, dst, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                (ex is IOException || ex is UnauthorizedAccessException)
                && attempt < MaxCopyAttempts)
            {
                Thread.Sleep(CopyRetryDelayMs);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new IOException(
                    $"could not write '{dst}' — the file is in use after {MaxCopyAttempts} attempts. " +
                    "Close PAX Cookbook (including its system tray icon) and run Setup again. " +
                    $"({ex.Message})", ex);
            }
        }
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
