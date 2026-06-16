using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3e -- adapter sidecar. The PowerShell broker computes the
// PAX invocation plan by calling Get-PaxInvocationPlan (located in
// app/broker/Pax/Adapter.psm1) on the same in-process runspace. The
// native broker is C# and cannot host PowerShell modules directly, so
// Stage 3e spawns a HIDDEN ONE-SHOT pwsh.exe child that:
//
//   1. Imports the production Adapter.psm1.
//   2. Loads the recipe JSON as a hashtable via ConvertFrom-Json
//      -AsHashtable -Depth 12 -DateKind String.
//   3. Calls Get-PaxInvocationPlan with -ExecutionMode 'local-manual'.
//   4. Pipes the resulting hashtable to ConvertTo-Json -Depth 8 -Compress.
//   5. Writes the JSON to stdout. Any failure path is written to
//      stderr and the process exits non-zero.
//
// The sidecar is in scope of Stage 3e's "hidden one-shot pwsh"
// permission: short-lived (default 10s timeout), CreateNoWindow=true,
// UseShellExecute=false, no profile / no logo, no inherited stdin.
// It does NOT touch Start-Broker.ps1 and does NOT keep a long-running
// PowerShell runspace alive.
//
// Doctrine:
//   - The recipe payload is passed via a TEMP FILE under the per-call
//     temp directory rather than the command line so it cannot
//     overflow the Windows ARG_MAX (32 KB).
//   - The temp file is deleted in a finally block regardless of the
//     sidecar outcome.
//   - The sidecar process is started with the adapter module path,
//     PAX script path, and recipe file path passed as positional
//     CLI arguments to a generated -Command script. Single quotes
//     in the values are escaped via PowerShell's '' rule.
//   - Timeouts are enforced. If the sidecar does not exit within
//     ProviderOptions.Timeout (default 10s), we Kill the process and
//     return a controlled AdapterSidecarFailed result.
//   - Adapter THROWS (recipe rejected by the validator) are routed
//     to RecipeInvalid; non-zero exit without throw text is routed
//     to AdapterSidecarFailed.
public sealed class PaxInvocationPlanProvider
{
    private readonly string _pwshPath;
    private readonly string _adapterModulePath;
    private readonly TimeSpan _timeout;

    public PaxInvocationPlanProvider(string pwshPath, string adapterModulePath,
        TimeSpan? timeout = null)
    {
        _pwshPath          = pwshPath;
        _adapterModulePath = adapterModulePath;
        _timeout           = timeout ?? TimeSpan.FromSeconds(10);
    }

    public PaxInvocationPlanResult Resolve(string recipeJson, string paxScriptPath)
    {
        if (string.IsNullOrWhiteSpace(_pwshPath))
        {
            return PaxInvocationPlanResult.SidecarFailed("pwsh_path_not_configured");
        }
        if (string.IsNullOrWhiteSpace(_adapterModulePath)
            || !File.Exists(_adapterModulePath))
        {
            return PaxInvocationPlanResult.SidecarFailed(
                "adapter_module_missing: " + _adapterModulePath);
        }

        // Stage the recipe JSON to a temp file. The PS sidecar reads
        // this file with -Raw + ConvertFrom-Json -AsHashtable so we do
        // NOT have to embed the recipe in the command line.
        var tempDir = Path.Combine(Path.GetTempPath(),
            "PAXCookbookSidecar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var recipeFile = Path.Combine(tempDir, "recipe.json");

        try
        {
            File.WriteAllText(recipeFile, recipeJson,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var script = BuildSidecarScript(
                adapterModulePath: _adapterModulePath,
                recipeFilePath:    recipeFile,
                paxScriptPath:     paxScriptPath);

            var psi = new ProcessStartInfo
            {
                FileName               = _pwshPath,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                RedirectStandardInput  = true,
                WorkingDirectory       = tempDir,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(script);

            using var proc = new Process { StartInfo = psi };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) stdout.AppendLine(e.Data);
            };
            proc.ErrorDataReceived  += (_, e) =>
            {
                if (e.Data is not null) stderr.AppendLine(e.Data);
            };

            try
            {
                if (!proc.Start())
                {
                    return PaxInvocationPlanResult.SidecarFailed("sidecar_start_failed");
                }
            }
            catch (Exception ex)
            {
                return PaxInvocationPlanResult.SidecarFailed(
                    "sidecar_start_failed: " + ex.Message);
            }

            proc.StandardInput.Close();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (!proc.WaitForExit((int)_timeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return PaxInvocationPlanResult.SidecarFailed(
                    "sidecar_timeout_after_" + (int)_timeout.TotalMilliseconds + "ms");
            }
            // Drain the async readers; WaitForExit() with no parameter
            // flushes the OutputDataReceived buffer.
            proc.WaitForExit();

            var exit = proc.ExitCode;
            var outText = stdout.ToString().Trim();
            var errText = stderr.ToString().Trim();

            if (exit != 0)
            {
                // Adapter throws are routed to RecipeInvalid; the
                // sidecar marks adapter throws by prefixing stderr
                // with "ADAPTER_THROW:" so we can disambiguate them
                // from generic sidecar failures (host crash, file
                // read error, ConvertTo-Json failure, ...).
                const string adapterThrowSentinel = "ADAPTER_THROW:";
                if (errText.StartsWith(adapterThrowSentinel, StringComparison.Ordinal))
                {
                    var msg = errText.Substring(adapterThrowSentinel.Length).Trim();
                    return PaxInvocationPlanResult.RecipeRejected(msg);
                }
                return PaxInvocationPlanResult.SidecarFailed(
                    "sidecar_exit_" + exit + ": " +
                    (errText.Length > 0 ? errText : outText));
            }

            if (outText.Length == 0)
            {
                return PaxInvocationPlanResult.SidecarFailed("sidecar_no_output");
            }

            PaxInvocationPlan plan;
            try
            {
                plan = ParsePlan(outText);
            }
            catch (Exception ex)
            {
                return PaxInvocationPlanResult.SidecarFailed(
                    "sidecar_json_parse_failed: " + ex.Message);
            }

            return PaxInvocationPlanResult.Ok(plan);
        }
        catch (Exception ex)
        {
            return PaxInvocationPlanResult.SidecarFailed("sidecar_exception: " + ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            }
            catch { }
        }
    }

    private static PaxInvocationPlan ParsePlan(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("plan_root_not_object");
        }

        string[] paxArgv      = ReadStringArray(root, "paxArgv");
        string   extraArgs    = ReadStringField(root, "extraArguments") ?? string.Empty;
        string   paxCommand   = ReadStringField(root, "paxCommand")     ?? string.Empty;
        string[] spawnArgv    = ReadStringArray(root, "spawnArgv");
        string   spawnCommand = ReadStringField(root, "spawnCommand")   ?? string.Empty;
        string   paxPath      = ReadStringField(root, "paxScriptPath")  ?? string.Empty;

        if (spawnArgv.Length < 4)
        {
            throw new InvalidDataException("plan_spawnArgv_too_short");
        }

        return new PaxInvocationPlan(
            PaxArgv:        paxArgv,
            ExtraArguments: extraArgs,
            PaxCommand:     paxCommand,
            SpawnArgv:      spawnArgv,
            SpawnCommand:   spawnCommand,
            PaxScriptPath:  paxPath);
    }

    private static string[] ReadStringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return Array.Empty<string>();
        if (el.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>(el.GetArrayLength());
        foreach (var item in el.EnumerateArray())
        {
            list.Add(item.ValueKind == JsonValueKind.String
                ? (item.GetString() ?? string.Empty)
                : item.ToString());
        }
        return list.ToArray();
    }

    private static string? ReadStringField(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    // Builds the inline -Command script the sidecar pwsh runs. The
    // script imports Adapter.psm1, loads the recipe from disk,
    // invokes Get-PaxInvocationPlan, and writes a compact JSON
    // projection to stdout. Adapter throws are caught and re-written
    // to stderr with the ADAPTER_THROW: sentinel so the C# side can
    // disambiguate recipe rejection from sidecar failure.
    private static string BuildSidecarScript(string adapterModulePath,
        string recipeFilePath, string paxScriptPath)
    {
        // PowerShell single-quote escaping: ' -> ''
        string Q(string v) => v.Replace("'", "''");

        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("try {");
        sb.AppendLine("  Import-Module '" + Q(adapterModulePath) + "' -Force");
        sb.AppendLine("  $raw = Get-Content -LiteralPath '" + Q(recipeFilePath) + "' -Raw -Encoding utf8");
        sb.AppendLine("  $recipe = $raw | ConvertFrom-Json -AsHashtable -Depth 12 -DateKind String");
        sb.AppendLine("  $plan = Get-PaxInvocationPlan -Recipe $recipe -PaxScriptPath '"
            + Q(paxScriptPath) + "' -ExecutionMode 'local-manual'");
        sb.AppendLine("  $plan | ConvertTo-Json -Depth 8 -Compress");
        sb.AppendLine("  exit 0");
        sb.AppendLine("} catch {");
        sb.AppendLine("  [Console]::Error.WriteLine('ADAPTER_THROW:' + $_.Exception.Message)");
        sb.AppendLine("  exit 1");
        sb.AppendLine("}");
        return sb.ToString();
    }
}

public enum PaxInvocationPlanStatus
{
    Ok,
    RecipeRejected,
    SidecarFailed,
}

public sealed record PaxInvocationPlanResult(
    PaxInvocationPlanStatus Status,
    PaxInvocationPlan?      Plan,
    string?                 Detail)
{
    public static PaxInvocationPlanResult Ok(PaxInvocationPlan plan) =>
        new(PaxInvocationPlanStatus.Ok, plan, null);

    public static PaxInvocationPlanResult RecipeRejected(string detail) =>
        new(PaxInvocationPlanStatus.RecipeRejected, null, detail);

    public static PaxInvocationPlanResult SidecarFailed(string detail) =>
        new(PaxInvocationPlanStatus.SidecarFailed, null, detail);
}
