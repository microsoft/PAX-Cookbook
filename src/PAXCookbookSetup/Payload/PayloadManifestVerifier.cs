using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using PAXCookbook.Shared.Contracts;

namespace PAXCookbookSetup.Payload;

// Verifies the staged payload folder against its manifest.json:
//   * every Payload.Files[].relativeInstallPath exists under root.
//   * sha256 + sizeBytes match.
//   * AppExe exists + matches its declared hash/size.
//   * SetupExe is metadata only — in the bootstrapper model it is NOT shipped
//     inside the payload (it is downloaded separately for repair/update), so it
//     is verified ONLY when a build actually embeds it in the payload.
//
// Used by EmbeddedPayloadSourceResolver consumers BEFORE handing the
// extracted folder to InstallVerb/UpdateVerb/RepairVerb. The existing
// ManifestValidator covers the same ground for the directory case
// (--payload-root); this verifier exists to keep the embedded path
// safe even if ManifestValidator's contract changes later.
public static class PayloadManifestVerifier
{
    public sealed record VerifyResult(bool Ok, IReadOnlyList<string> Errors);

    public static VerifyResult Verify(string payloadRoot, Manifest m)
    {
        var errors = new List<string>();
        CheckFile(payloadRoot, m.Payload.AppExe.RelativeInstallPath,
                  m.Payload.AppExe.Sha256, m.Payload.AppExe.SizeBytes, errors);
        // SetupExe is metadata only in the bootstrapper model (not shipped in the
        // payload). Verify it ONLY when a build actually embeds it in the payload.
        if (File.Exists(Path.Combine(payloadRoot, m.Payload.SetupExe.Name)))
            CheckFile(payloadRoot, m.Payload.SetupExe.Name,
                      m.Payload.SetupExe.Sha256, m.Payload.SetupExe.SizeBytes, errors);
        foreach (var f in m.Payload.Files)
            CheckFile(payloadRoot, f.RelativeInstallPath, f.Sha256, f.SizeBytes, errors);
        return new VerifyResult(errors.Count == 0, errors);
    }

    private static void CheckFile(string root, string rel, string expectedSha,
                                  long expectedSize, List<string> errors)
    {
        if (rel.Contains("..") || Path.IsPathRooted(rel))
        {
            errors.Add($"unsafe manifest path: {rel}");
            return;
        }
        var path = Path.Combine(root, rel);
        if (!File.Exists(path)) { errors.Add($"missing: {rel}"); return; }
        var sz = new FileInfo(path).Length;
        if (sz != expectedSize)
        {
            errors.Add($"size mismatch: {rel} expected={expectedSize} actual={sz}");
            return;
        }
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(fs);
        var hex = ToHex(hash);
        if (!hex.Equals(expectedSha, System.StringComparison.OrdinalIgnoreCase))
            errors.Add($"sha256 mismatch: {rel} expected={expectedSha} actual={hex}");
    }

    private static string ToHex(byte[] b)
    {
        var c = new char[b.Length * 2];
        const string hex = "0123456789abcdef";
        for (int i = 0; i < b.Length; i++)
        {
            c[i * 2]     = hex[b[i] >> 4];
            c[i * 2 + 1] = hex[b[i] & 0xF];
        }
        return new string(c);
    }
}
