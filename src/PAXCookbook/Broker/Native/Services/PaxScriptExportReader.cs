using System.Text.RegularExpressions;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-A -- bundled PAX script export reader.
//
// Parity with Routes/Runtime.ps1:Invoke-RuntimeExportPaxScriptGet
// (GET /api/v1/runtime/pax-script/download):
//   * Reads the bundled PAX script bytes via File.ReadAllBytes.
//   * Returns 500 pax_script_unavailable when the file is missing.
//   * Returns 500 pax_script_read_failed when File.ReadAllBytes
//     throws.
//   * Sanitises the bundled version against the conservative regex
//     ^[A-Za-z0-9.\-]{1,40}$. Failure yields the literal string
//     "unknown" so the Content-Disposition filename remains safe.
//   * Builds the filename literal
//     'PAX_Purview_Audit_Log_Processor_v' + version + '.ps1'.
//   * No client-supplied path -- the script path is locked to the
//     bundled location from NativeBrokerHostOptions.
public sealed class PaxScriptExportReader
{
    private static readonly Regex VersionAllowlist = new(
        @"^[A-Za-z0-9.\-]{1,40}$",
        RegexOptions.Compiled);

    private readonly string _paxScriptPath;
    private readonly string _paxScriptVersion;

    public PaxScriptExportReader(string paxScriptPath, string? paxScriptVersion)
    {
        _paxScriptPath    = paxScriptPath ?? string.Empty;
        _paxScriptVersion = paxScriptVersion ?? string.Empty;
    }

    public PaxScriptExportOutcome Read()
    {
        if (string.IsNullOrWhiteSpace(_paxScriptPath))
        {
            return PaxScriptExportOutcome.Unavailable(
                "Bundled PAX script path is not configured.");
        }
        if (!File.Exists(_paxScriptPath))
        {
            return PaxScriptExportOutcome.Unavailable(
                "Bundled PAX script file is missing.");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(_paxScriptPath);
        }
        catch (Exception ex)
        {
            return PaxScriptExportOutcome.ReadFailed(ex.Message);
        }

        var versionSegment =
            !string.IsNullOrEmpty(_paxScriptVersion) &&
            VersionAllowlist.IsMatch(_paxScriptVersion)
                ? _paxScriptVersion
                : "unknown";

        var filename = "PAX_Purview_Audit_Log_Processor_v"
                     + versionSegment + ".ps1";

        return PaxScriptExportOutcome.Success(bytes, filename);
    }
}

public sealed record PaxScriptExportOutcome(
    bool    Ok,
    string? Error,
    string? Detail,
    byte[]? Bytes,
    string? Filename)
{
    public static PaxScriptExportOutcome Success(byte[] bytes, string filename) =>
        new(true, null, null, bytes, filename);

    public static PaxScriptExportOutcome Unavailable(string detail) =>
        new(false, "pax_script_unavailable", detail, null, null);

    public static PaxScriptExportOutcome ReadFailed(string detail) =>
        new(false, "pax_script_read_failed", detail, null, null);
}
