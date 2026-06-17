using PAXCookbook.Shared.Contracts;
using PAXCookbook.Shared.Hashing;
using PAXCookbook.Shared.Io;

namespace PAXCookbookSetup;

public sealed record ManifestValidationResult(bool Ok, List<string> Errors)
{
    public static ManifestValidationResult Pass() => new(true, new());
    public static ManifestValidationResult Fail(string e) => new(false, new() { e });
}

public static class ManifestValidator
{
    // Validate manifest schema fundamentals + every payload file under
    // payloadRoot matches sha256+size and has a safe relativeInstallPath.
    public static ManifestValidationResult Validate(Manifest m, string payloadRoot)
    {
        var errors = new List<string>();
        if (m.Product != "PAXCookbook") errors.Add($"product must be PAXCookbook (got {m.Product})");
        if (m.ManifestSchemaVersion != 1) errors.Add($"manifestSchemaVersion must be 1 (got {m.ManifestSchemaVersion})");
        if (string.IsNullOrWhiteSpace(m.AppVersion)) errors.Add("appVersion missing");
        if (string.IsNullOrWhiteSpace(m.SetupVersion)) errors.Add("setupVersion missing");
        if (m.TargetOs != "windows") errors.Add("targetOs must be windows");
        if (m.TargetArch != "x64") errors.Add("targetArch must be x64");

        // SetupExe under payloadRoot/<name>. Metadata only in the bootstrapper
        // model (downloaded separately, NOT shipped in the payload), so validate
        // it ONLY when a build actually embeds it in the payload.
        if (File.Exists(Path.Combine(payloadRoot, m.Payload.SetupExe.Name)))
            CheckEntry(payloadRoot, m.Payload.SetupExe.Name, m.Payload.SetupExe.Sha256,
                       m.Payload.SetupExe.SizeBytes, "payload.setupExe", errors);
        // AppExe at relativeInstallPath relative to payloadRoot too
        if (!SafePath.IsSafeRelative(m.Payload.AppExe.RelativeInstallPath))
            errors.Add($"payload.appExe.relativeInstallPath unsafe: {m.Payload.AppExe.RelativeInstallPath}");
        else
            CheckEntry(payloadRoot, m.Payload.AppExe.RelativeInstallPath,
                       m.Payload.AppExe.Sha256, m.Payload.AppExe.SizeBytes,
                       "payload.appExe", errors);

        foreach (var f in m.Payload.Files)
        {
            if (!SafePath.IsSafeRelative(f.RelativeInstallPath))
            {
                errors.Add($"payload.files entry has unsafe path: {f.RelativeInstallPath}");
                continue;
            }
            CheckEntry(payloadRoot, f.RelativeInstallPath, f.Sha256, f.SizeBytes,
                       $"payload.files[{f.RelativeInstallPath}]", errors);
        }

        return new ManifestValidationResult(errors.Count == 0, errors);
    }

    private static void CheckEntry(string payloadRoot, string rel, string expectedSha,
                                   long expectedSize, string label, List<string> errors)
    {
        string full;
        try { full = SafePath.CombineUnderRoot(payloadRoot, rel); }
        catch (Exception ex) { errors.Add($"{label}: {ex.Message}"); return; }

        if (!File.Exists(full)) { errors.Add($"{label}: file not found: {full}"); return; }
        var fi = new FileInfo(full);
        if (fi.Length != expectedSize)
            errors.Add($"{label}: size mismatch expected={expectedSize} actual={fi.Length}");
        var actualSha = Sha256Hash.OfFile(full);
        if (!Sha256Hash.Equal(actualSha, expectedSha))
            errors.Add($"{label}: sha256 mismatch expected={expectedSha} actual={actualSha}");
    }
}
