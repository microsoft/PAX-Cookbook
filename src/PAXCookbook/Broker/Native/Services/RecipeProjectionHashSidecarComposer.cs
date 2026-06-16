using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3h -- production IRecipeProjectionHashComposer. Spawns a
// hidden one-shot pwsh.exe that:
//   1. Imports the bundled Adapter.psm1 (which exports
//      Get-RecipeProjectionHash).
//   2. Reads the recipe file with ConvertFrom-Json -AsHashtable so
//      the PS function sees hashtable property accessors verbatim.
//   3. Loads the AuthProfile from a temp JSON staged by the C#
//      caller (also -AsHashtable). Null when the caller passes a
//      null AuthProfile.
//   4. Calls Get-RecipeProjectionHash with the supplied
//      ExecutionMode + PaxScriptVersion.
//   5. Writes the 64-char lowercase hex digest to stdout (no
//      trailing newline).
//
// Doctrine:
//   * Sidecar ArgumentList element-by-element; no shell parsing.
//   * Temp files for the recipe / auth-profile staging dodge
//     ARG_MAX and avoid embedding JSON into PS literals.
//   * Bounded timeout (default 30s -- the PS function is pure
//     compute and does not prompt the user).
//   * On any failure (spawn / timeout / non-zero exit / stdout not
//     a 64-char lowercase hex) returns Ok=false with a structured
//     Error message; the PUT route maps that to 500
//     projection_failed mirroring the PS broker.
//   * No secret material is ever staged. AuthProfile JSON contains
//     metadata only (clientId / certThumbprint / target name) -- the
//     PS Get-PaxArgvArray reads only those camelCase fields.
public sealed class RecipeProjectionHashSidecarComposer
    : IRecipeProjectionHashComposer
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly Regex Sha256HexLowerPattern = new(
        @"^[0-9a-f]{64}$",
        RegexOptions.Compiled);

    private readonly string _pwshPath;
    private readonly string _adapterModulePath;
    private readonly TimeSpan _timeout;

    public RecipeProjectionHashSidecarComposer(
        string pwshPath,
        string adapterModulePath,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pwshPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterModulePath);
        _pwshPath          = pwshPath;
        _adapterModulePath = adapterModulePath;
        _timeout           = timeout ?? DefaultTimeout;
    }

    public string PwshPath => _pwshPath;
    public string AdapterModulePath => _adapterModulePath;
    public TimeSpan Timeout => _timeout;

    public async Task<RecipeProjectionHashResult> ComposeAsync(
        string recipeFilePath,
        string paxScriptPath,
        AuthProfileRow? authProfile,
        string executionMode,
        string paxScriptVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(paxScriptPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionMode);
        ArgumentException.ThrowIfNullOrWhiteSpace(paxScriptVersion);

        if (!File.Exists(_pwshPath))
        {
            return Failure("pwsh_missing: " + _pwshPath);
        }
        if (!File.Exists(_adapterModulePath))
        {
            return Failure("adapter_module_missing: " + _adapterModulePath);
        }
        if (!File.Exists(recipeFilePath))
        {
            return Failure("recipe_file_missing: " + recipeFilePath);
        }

        string? authProfileFile = null;
        if (authProfile is not null)
        {
            authProfileFile = StageAuthProfileFile(authProfile);
        }

        try
        {
            return await RunSidecarAsync(
                recipeFilePath, paxScriptPath, authProfileFile,
                executionMode, paxScriptVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (authProfileFile is not null)
            {
                try { File.Delete(authProfileFile); } catch { }
            }
        }
    }

    // Project the AuthProfileRow into the camelCase shape the PS
    // Get-PaxArgvArray expects ($AuthProfile.clientId,
    // $AuthProfile.certThumbprint, etc.). The PS function reads
    // these properties off the hashtable; ConvertTo-Json + ConvertFrom-
    // Json -AsHashtable round-trip preserves the camelCase keys.
    private static string StageAuthProfileFile(AuthProfileRow row)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["authProfileId"]      = row.AuthProfileId,
            ["name"]                = row.Name,
            ["mode"]                = row.Mode,
            ["tenantId"]            = row.TenantId,
            ["clientId"]            = row.ClientId,
            ["credManTarget"]       = row.CredManTarget,
            ["certThumbprint"]      = row.CertThumbprint,
            ["certStore"]           = row.CertStore,
            ["description"]         = row.Description,
            ["lastVerifiedAt"]      = row.LastVerifiedAt,
            ["lastVerifiedResult"]  = row.LastVerifiedResult,
            ["createdAt"]           = row.CreatedAt,
            ["updatedAt"]           = row.UpdatedAt,
        };
        var json = JsonSerializer.Serialize(payload);
        var path = Path.Combine(Path.GetTempPath(),
            "pax-stage3h-authprofile-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    private async Task<RecipeProjectionHashResult> RunSidecarAsync(
        string recipeFilePath,
        string paxScriptPath,
        string? authProfileFile,
        string executionMode,
        string paxScriptVersion,
        CancellationToken cancellationToken)
    {
        var script = BuildSidecarScript(
            adapterModulePath: _adapterModulePath,
            recipeFilePath:    recipeFilePath,
            paxScriptPath:     paxScriptPath,
            authProfileFile:   authProfileFile,
            executionMode:     executionMode,
            paxScriptVersion:  paxScriptVersion);

        var psi = new ProcessStartInfo
        {
            FileName               = _pwshPath,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NoLogo");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) =>
            { if (e.Data is not null) stdoutBuilder.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) =>
            { if (e.Data is not null) stderrBuilder.AppendLine(e.Data); };

        try
        {
            if (!proc.Start())
            {
                return Failure("spawn_failed: pwsh.exe did not start.");
            }
        }
        catch (Exception ex)
        {
            return Failure("spawn_failed: " + ex.Message);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linked     = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (timeoutCts.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                return Failure("sidecar_timeout after "
                    + (int)_timeout.TotalMilliseconds + "ms");
            }
            throw;
        }

        var stdout = stdoutBuilder.ToString().Trim();
        var stderr = stderrBuilder.ToString();

        if (proc.ExitCode != 0)
        {
            return Failure("sidecar_exit_nonzero: code=" + proc.ExitCode
                + " stderr=" + stderr.Trim());
        }
        if (stdout.Length == 0)
        {
            return Failure("sidecar_empty_stdout: stderr=" + stderr.Trim());
        }
        if (!Sha256HexLowerPattern.IsMatch(stdout))
        {
            return Failure("sidecar_stdout_not_sha256_hex: " + stdout);
        }
        return new RecipeProjectionHashResult(
            Ok: true, Sha256Hex: stdout, Error: null);
    }

    // -Command body. Stages -AsHashtable conversions through
    // ConvertFrom-Json -Depth 32 -AsHashtable so the PS function
    // accessors ($Recipe.auth.mode etc.) work identically to the
    // PowerShell broker's Read-RecipeFile path.
    internal static string BuildSidecarScript(
        string adapterModulePath,
        string recipeFilePath,
        string paxScriptPath,
        string? authProfileFile,
        string executionMode,
        string paxScriptVersion)
    {
        var adapterEsc = EscapeSingleQuoted(adapterModulePath);
        var recipeEsc  = EscapeSingleQuoted(recipeFilePath);
        var paxEsc     = EscapeSingleQuoted(paxScriptPath);
        var modeEsc    = EscapeSingleQuoted(executionMode);
        var verEsc     = EscapeSingleQuoted(paxScriptVersion);

        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference='Stop';");
        sb.Append(" Import-Module '").Append(adapterEsc).Append("' -Force;");
        sb.Append(" $recipe = (Get-Content -LiteralPath '")
          .Append(recipeEsc)
          .Append("' -Raw -Encoding utf8) | ConvertFrom-Json -Depth 32 -AsHashtable;");

        if (authProfileFile is null)
        {
            sb.Append(" $authProfile = $null;");
        }
        else
        {
            var apEsc = EscapeSingleQuoted(authProfileFile);
            sb.Append(" $authProfile = (Get-Content -LiteralPath '")
              .Append(apEsc)
              .Append("' -Raw -Encoding utf8) | ConvertFrom-Json -Depth 32 -AsHashtable;");
        }

        sb.Append(" $h = Get-RecipeProjectionHash -Recipe $recipe");
        sb.Append(" -PaxScriptPath '").Append(paxEsc).Append("'");
        sb.Append(" -AuthProfile $authProfile");
        sb.Append(" -ExecutionMode '").Append(modeEsc).Append("'");
        sb.Append(" -PaxScriptVersion '").Append(verEsc).Append("';");
        sb.Append(" [Console]::Out.Write($h);");

        return sb.ToString();
    }

    private static string EscapeSingleQuoted(string s) =>
        s.Replace("'", "''");

    private static RecipeProjectionHashResult Failure(string detail) =>
        new(Ok: false, Sha256Hex: null, Error: detail);
}
