using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.ExitCodes;
using PAXCookbook.Shared.Versioning;
using PAXCookbookSetup.Shell;

namespace PAXCookbookSetup.Verbs;

public static class UpdateVerb
{
    // Runs an update (or repair when isRepair=true). Returns exit code.
    public static int Run(ParsedArgs args, Manifest m, string payloadRoot,
                          string installRoot, SetupLogger log,
                          bool isRepair = false,
                          IPayloadOperations? payloadOps = null,
                          IShellOperations? shellOps = null)
    {
        payloadOps ??= DefaultPayloadOperations.Instance;
        var kindLabel = isRepair ? "repair" : "update";
        log.Write($"{kindLabel}-begin", fields: new Dictionary<string, object?>
        {
            ["installRoot"] = installRoot, ["targetAppVersion"] = m.AppVersion
        });

        // Step 1: validate manifest.
        var v = ManifestValidator.Validate(m, payloadRoot);
        if (!v.Ok)
        {
            log.Write($"{kindLabel}-manifest-invalid", "error",
                new Dictionary<string, object?> { ["errors"] = string.Join("; ", v.Errors) });
            return isRepair ? SetupExitCodes.RepairFailed : SetupExitCodes.UpdateFailed;
        }

        // Step 2: validate current install-state.
        var existing = InstallStateStore.TryLoad(installRoot);
        if (existing is null)
        {
            log.Write($"{kindLabel}-state-invalid", "error");
            return SetupExitCodes.IntegrityCheckFailed;
        }

        // Step 3: version analysis.
        var cur = SemVer.Parse(existing.AppVersion);
        var tgt = SemVer.Parse(m.AppVersion);
        var cmp = tgt.CompareTo(cur);
        string reason;
        string opKind;
        if (cmp == 0)
        {
            reason = "repair";
            opKind = isRepair ? "repair" : "repair";
            if (!isRepair && !args.IsSameVersionRepair)
            {
                log.Write($"{kindLabel}-same-version-noop");
                return SetupExitCodes.Ok;
            }
        }
        else if (cmp < 0)
        {
            // Downgrade.
            if (!args.AllowDowngrade && !isRepair)
            {
                log.Write($"{kindLabel}-downgrade-blocked", "error",
                    new Dictionary<string, object?>
                    { ["current"] = existing.AppVersion, ["target"] = m.AppVersion });
                return SetupExitCodes.DowngradeBlocked;
            }
            reason = "downgrade";
            opKind = "downgrade";
        }
        else
        {
            reason = "update";
            opKind = "update";
        }
        if (isRepair) { reason = "repair"; opKind = "repair"; }

        var appRoot = Path.Combine(installRoot, "App");

        // Step 4: snapshot.
        string snapshot;
        try
        {
            snapshot = SnapshotEngine.Create(installRoot, appRoot, existing.AppVersion);
            log.Write($"{kindLabel}-snapshot-created",
                fields: new Dictionary<string, object?> { ["path"] = snapshot });
        }
        catch (Exception ex)
        {
            log.Write($"{kindLabel}-snapshot-failed", "error",
                new Dictionary<string, object?> { ["detail"] = ex.Message });
            return isRepair ? SetupExitCodes.RepairFailed : SetupExitCodes.UpdateFailed;
        }

        // Step 5: replace files + re-verify.
        try
        {
            payloadOps.Copy(m, payloadRoot, installRoot, appRoot);
            payloadOps.VerifyInstalled(m, installRoot);
        }
        catch (Exception ex)
        {
            log.Write($"{kindLabel}-replace-failed", "error",
                new Dictionary<string, object?> { ["detail"] = ex.Message });
            // Rollback.
            try
            {
                SnapshotEngine.Restore(snapshot, appRoot);
                var restoredState = existing with
                {
                    LastOperation = new LastOperation
                    {
                        Kind = opKind, Status = "rolled-back",
                        At = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                        Detail = ex.Message
                    }
                };
                InstallStateStore.Save(installRoot, restoredState);
                log.Write($"{kindLabel}-rollback-ok");
                return SetupExitCodes.RollbackPerformed;
            }
            catch (Exception rex)
            {
                log.Write($"{kindLabel}-rollback-failed", "error",
                    new Dictionary<string, object?> { ["detail"] = rex.Message });
                return SetupExitCodes.RollbackFailed;
            }
        }

        // Step 6: update install-state.
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var pvList = existing.PreviousVersions is null
            ? new List<PreviousVersion>()
            : new List<PreviousVersion>(existing.PreviousVersions);
        pvList.Add(new PreviousVersion
        {
            AppVersion = existing.AppVersion,
            SetupVersion = existing.SetupVersion,
            At = now,
            Reason = reason
        });
        var newState = existing with
        {
            AppVersion = m.AppVersion,
            SetupVersion = m.SetupVersion,
            AppExeVersion = m.AppVersion,
            UpdatedAtUtc = now,
            PreviousVersions = pvList,
            LastOperation = new LastOperation
            {
                Kind = opKind, Status = "ok", At = now, ExitCode = 0
            }
        };
        InstallStateStore.Save(installRoot, newState);

        // Phase 8: reconcile shell identity (shortcuts + protocol + ARP).
        if (shellOps is not null)
        {
            try
            {
                var sr = isRepair
                    ? shellOps.Repair(installRoot, m.AppVersion)
                    : shellOps.Reconcile(installRoot, m.AppVersion);
                log.Write($"{kindLabel}-shell-reconciled",
                    fields: new Dictionary<string, object?>
                    {
                        ["shortcutsCreated"] = sr.ShortcutsCreated,
                        ["protocolRegistered"] = sr.ProtocolRegistered,
                        ["uninstallRegistered"] = sr.UninstallRegistered,
                        ["fileAssociationsRegistered"] = sr.FileAssociationsRegistered
                    });
            }
            catch (Exception ex)
            {
                log.Write($"{kindLabel}-shell-failed", "warn",
                    new Dictionary<string, object?> { ["detail"] = ex.Message });
            }
        }

        log.Write($"{kindLabel}-complete");
        return SetupExitCodes.Ok;
    }
}
