using System.Security.Cryptography;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3e -- bundled PAX script integrity check on the manual cook
// entry path. Mirrors the "re-hash and compare" block in
// Routes/Cooks.ps1:Invoke-CookStart (~line 1360) which calls
// Get-FileHash -Algorithm SHA256 against the bundled PAX script and
// rejects the cook with HTTP 500 / pax_script_integrity if the
// observed hash differs from the broker's cached baseline. The
// baseline comes from app/VERSION.json's paxScript.sha256 (loaded
// once at startup by VersionInfoReader).
//
// Doctrine:
//   - VERSION.json must be loadable -- otherwise the broker has no
//     baseline to compare against. Surface as version_file_missing /
//     500. This is intentionally STRICT: a manual cook MUST NOT run
//     against a PAX script with an unknown expected hash.
//   - The script file itself must exist on disk and be hashable. A
//     missing file -> 500 pax_script_missing.
//   - Hash comparison is case-insensitive hex (both sides upper-cased
//     before Equal).
//   - The verifier is pure -- it does NOT mutate, does NOT delete,
//     does NOT log; the caller routes the result to a controlled
//     response envelope.
public sealed class PaxScriptIntegrityVerifier
{
    private readonly VersionInfo _versionInfo;
    private readonly string      _paxScriptPath;

    public PaxScriptIntegrityVerifier(VersionInfo versionInfo, string paxScriptPath)
    {
        _versionInfo   = versionInfo;
        _paxScriptPath = paxScriptPath;
    }

    public string PaxScriptPath => _paxScriptPath;

    public string? ExpectedSha256 =>
        _versionInfo.IsAvailable
            ? _versionInfo.BundledPax?.Sha256
            : null;

    public string? PaxScriptVersion =>
        _versionInfo.IsAvailable
            ? _versionInfo.BundledPax?.Version
            : null;

    public PaxIntegrityResult Verify()
    {
        if (!_versionInfo.IsAvailable
            || _versionInfo.BundledPax is null
            || string.IsNullOrWhiteSpace(_versionInfo.BundledPax.Sha256))
        {
            // No baseline -> refuse. Stage 3e does NOT silently fall
            // through to a "compute and trust" branch -- that would
            // defeat the bundled-PAX integrity contract.
            return PaxIntegrityResult.NoBaseline(
                _versionInfo.LoadError ?? "version_file_missing");
        }

        if (string.IsNullOrWhiteSpace(_paxScriptPath))
        {
            return PaxIntegrityResult.MissingScript(_paxScriptPath);
        }
        if (!File.Exists(_paxScriptPath))
        {
            return PaxIntegrityResult.MissingScript(_paxScriptPath);
        }

        string actual;
        try
        {
            // SHA256.HashData over the raw file bytes -- matches
            // Get-FileHash -Algorithm SHA256 byte-for-byte. The
            // PowerShell broker uses ReadAllBytes for its rehash
            // step, so we do the same: the script is < 200 KB.
            actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_paxScriptPath)));
        }
        catch (Exception ex)
        {
            return PaxIntegrityResult.HashFailed(ex.Message);
        }

        var expected = _versionInfo.BundledPax.Sha256.Trim().ToUpperInvariant();
        var actualUp = actual.ToUpperInvariant();

        return string.Equals(expected, actualUp, StringComparison.Ordinal)
            ? PaxIntegrityResult.Match(expected, actualUp)
            : PaxIntegrityResult.Mismatch(expected, actualUp);
    }
}

public sealed record PaxIntegrityResult(
    PaxIntegrityStatus Status,
    string?            Expected,
    string?            Actual,
    string?            Detail)
{
    public bool IsMatch => Status == PaxIntegrityStatus.Match;

    public static PaxIntegrityResult Match(string expected, string actual) =>
        new(PaxIntegrityStatus.Match, expected, actual, null);

    public static PaxIntegrityResult Mismatch(string expected, string actual) =>
        new(PaxIntegrityStatus.Mismatch, expected, actual, null);

    public static PaxIntegrityResult MissingScript(string path) =>
        new(PaxIntegrityStatus.MissingScript, null, null, path);

    public static PaxIntegrityResult NoBaseline(string detail) =>
        new(PaxIntegrityStatus.NoBaseline, null, null, detail);

    public static PaxIntegrityResult HashFailed(string detail) =>
        new(PaxIntegrityStatus.HashFailed, null, null, detail);
}

public enum PaxIntegrityStatus
{
    Match,
    Mismatch,
    MissingScript,
    NoBaseline,
    HashFailed,
}
