using PAXCookbook.Shared;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.Shared.Hashing;

namespace PAXCookbookSetup;

// Live self-handoff per setup-self-handoff-contract.md §3-§9 + §20.
public static class SelfHandoff
{
    public sealed record TempCopy(string TempFolder, string TempExePath, string Sha256);
    public sealed record MarkerValidation(bool Ok, string? Error);

    public static bool ShouldHandOff(string runningExePath, string installRoot,
                                     bool handoffFromInstalled)
    {
        if (handoffFromInstalled) return false;
        // A self-contained single-file bootstrapper has no on-disk managed
        // assembly (Assembly.Location is empty), and is never run from under the
        // install root, so there is nothing to hand off. The installed
        // framework-dependent Setup DLL, by contrast, lives under installRoot.
        if (string.IsNullOrWhiteSpace(runningExePath)) return false;
        var runningFull = Path.GetFullPath(runningExePath);
        var rootFull = Path.GetFullPath(installRoot)
                         .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return runningFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
    }

    public static string CreateTempFolder(string baseTemp)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var utc = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var rand = Guid.NewGuid().ToString("N").Substring(0, 8);
            var p = Path.Combine(baseTemp, $"PAXCookbookSetup_{utc}_{rand}");
            if (!Directory.Exists(p))
            {
                Directory.CreateDirectory(p);
                return p;
            }
        }
        throw new IOException("could not create unique temp handoff folder after 5 attempts");
    }

    public static TempCopy CopySelfToTemp(string runningPath, string baseTemp)
    {
        var temp = CreateTempFolder(baseTemp);
        // Preserve the primary file's own name (PAXCookbookSetup.dll for the
        // installed framework-dependent Setup) so dotnet.exe can run the temp
        // copy with the same assembly name.
        var primaryName = Path.GetFileName(runningPath);
        var dst = Path.Combine(temp, primaryName);
        File.Copy(runningPath, dst, overwrite: false);
        var srcSha = Sha256Hash.OfFile(runningPath);
        var dstSha = Sha256Hash.OfFile(dst);
        if (!Sha256Hash.Equal(srcSha, dstSha))
            throw new IOException($"hash mismatch after copy: src={srcSha} dst={dstSha}");

        // Framework-dependent .NET apps need their .runtimeconfig.json /
        // .deps.json / referenced-DLL siblings to boot. Copy every OTHER file in
        // the source directory so the temp copy is a self-sufficient runner.
        // Excludes the primary (already copied) and any log files.
        var srcDir = Path.GetDirectoryName(runningPath)!;
        foreach (var f in Directory.EnumerateFiles(srcDir))
        {
            var name = Path.GetFileName(f);
            if (string.Equals(name, primaryName, StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) continue;
            var to = Path.Combine(temp, name);
            try { File.Copy(f, to, overwrite: false); } catch { /* best effort */ }
        }

        return new TempCopy(temp, dst, dstSha);
    }

    // Builds the validated arg vector to pass to the temp copy. We do
    // NOT forward the raw argv — we reconstruct from ParsedArgs so a
    // hostile arg slipped past the parser cannot make it across.
    public static List<string> BuildHandoffArgs(ParsedArgs original, string tempFolder,
                                                string installRootForChild)
    {
        var args = new List<string> { original.Verb };
        args.Add("--install-root"); args.Add(installRootForChild);
        if (!string.IsNullOrEmpty(original.PayloadRoot))
        {
            args.Add("--payload-root"); args.Add(original.PayloadRoot!);
        }
        if (original.Force) args.Add("--force");
        if (original.ReinstallSameVersion) args.Add("--reinstall-same-version");
        if (original.AllowDowngrade) args.Add("--allow-downgrade");
        if (original.DryRun) args.Add("--dry-run");
        if (original.RemoveUserData) args.Add("--remove-user-data");
        if (original.ConfirmRemoveUserData) args.Add("--confirm-remove-user-data");
        if (original.GuiUninstall) args.Add("--gui-uninstall");
        args.Add("--handoff-from-installed");
        args.Add("--handoff-folder"); args.Add(tempFolder);
        return args;
    }

    public static MarkerValidation ValidateMarkers(string runningExePath, ParsedArgs args,
                                                   string systemTemp)
    {
        if (!args.HandoffFromInstalled)
            return new(false, "missing --handoff-from-installed");
        if (string.IsNullOrEmpty(args.HandoffFolder))
            return new(false, "missing --handoff-folder");
        var folderFull = Path.GetFullPath(args.HandoffFolder!);
        var runningDir = Path.GetFullPath(Path.GetDirectoryName(runningExePath)!);
        if (!string.Equals(folderFull, runningDir, StringComparison.OrdinalIgnoreCase))
            return new(false, $"--handoff-folder ({folderFull}) does not match running EXE dir ({runningDir})");
        var tempFull = Path.GetFullPath(systemTemp)
                         .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!folderFull.StartsWith(tempFull, StringComparison.OrdinalIgnoreCase))
            return new(false, $"--handoff-folder is not under %TEMP%: {folderFull}");
        return new(true, null);
    }

    public static void CleanupTempFolder(string tempFolder, IDeferredDeleter deferred,
                                         SetupLogger log)
    {
        try
        {
            if (Directory.Exists(tempFolder))
            {
                Directory.Delete(tempFolder, recursive: true);
                log.Write("handoff-temp-deleted",
                    fields: new Dictionary<string, object?> { ["path"] = tempFolder });
            }
        }
        catch (Exception ex)
        {
            foreach (var f in SafeEnumerate(tempFolder))
                deferred.ScheduleDeleteOnReboot(f);
            deferred.ScheduleDeleteOnReboot(tempFolder);
            log.Write("handoff-temp-deferred-delete", "warn",
                fields: new Dictionary<string, object?>
                { ["path"] = tempFolder, ["detail"] = ex.Message });
        }
    }

    private static IEnumerable<string> SafeEnumerate(string root)
    {
        try { return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); }
        catch { return Array.Empty<string>(); }
    }
}

public sealed record HandoffResult(int ExitCode, string TempFolder, string TempExePath, string Sha256);

public static class HandoffRunner
{
    public static HandoffResult Run(ParsedArgs original, string runningExePath,
                                    string installRoot, string baseTemp,
                                    IProcessLauncher launcher, SetupLogger log)
    {
        log.Write("handoff-begin", fields: new Dictionary<string, object?>
        {
            ["runningExe"] = runningExePath, ["installRoot"] = installRoot
        });
        var copy = SelfHandoff.CopySelfToTemp(runningExePath, baseTemp);
        log.Write("handoff-temp-copied", fields: new Dictionary<string, object?>
        {
            ["tempExe"] = copy.TempExePath, ["sha256"] = copy.Sha256
        });
        var args = SelfHandoff.BuildHandoffArgs(original, copy.TempFolder, installRoot);
        try
        {
            // WDAC-safe relaunch: run the temp Setup DLL through the signed
            // dotnet.exe host (dotnet.exe "<temp>\PAXCookbookSetup.dll" <args>),
            // never the unsigned apphost.
            var dotnet = DotNetLaunch.DotNetExePath();
            var launchArgs = new List<string> { copy.TempExePath };
            launchArgs.AddRange(args);
            launcher.Start(dotnet, launchArgs);
            log.Write("handoff-launched", fields: new Dictionary<string, object?>
            {
                ["dotnet"] = dotnet,
                ["tempDll"] = copy.TempExePath,
                ["args"] = string.Join(" ", launchArgs)
            });
            return new HandoffResult(SetupExitCodes.Ok, copy.TempFolder, copy.TempExePath, copy.Sha256);
        }
        catch (Exception ex)
        {
            log.Write("handoff-launch-failed", "error",
                fields: new Dictionary<string, object?> { ["detail"] = ex.Message });
            return new HandoffResult(SetupExitCodes.HandoffFailed, copy.TempFolder,
                                     copy.TempExePath, copy.Sha256);
        }
    }
}
