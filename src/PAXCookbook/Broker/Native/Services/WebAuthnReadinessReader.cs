using System.Text.Json;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Read-only adapter over <WorkspacePath>\Auth\webauthn-credentials.json.
//
// Mirrors Get-WebAuthnRegistrationSummary in app/broker/Auth/WebAuthnVerify.ps1:
//   - missing file        -> registered:false, credentialIds:[]
//   - present + parseable -> registered:credentials.Length > 0,
//                            credentialIds:[<credentialId>...]
//   - present + corrupt   -> registered:false, credentialIds:[]
//                            (Stage 3d declines to throw because this
//                             is a status surface; the PowerShell broker
//                             throws, but Stage 3d's native surface
//                             would only surface a 500 to the SPA which
//                             is less actionable than an honest
//                             "not registered" verdict.)
//
// Public keys are NEVER surfaced -- only the credentialId list. This
// matches the PS broker's defensive doctrine: the SPA does not need
// the public key; the broker is the verifier.
public sealed class WebAuthnReadinessReader
{
    private readonly string? _credentialsFilePath;

    public WebAuthnReadinessReader(string? workspaceFolderPath)
    {
        if (string.IsNullOrWhiteSpace(workspaceFolderPath))
        {
            _credentialsFilePath = null;
        }
        else
        {
            _credentialsFilePath = Path.Combine(
                workspaceFolderPath, "Auth", "webauthn-credentials.json");
        }
    }

    public WebAuthnStatusInfo BuildStatus(int port)
    {
        var (registered, credentialIds) = ReadCredentialIds();
        return new WebAuthnStatusInfo(
            Registered: registered,
            CredentialIds: credentialIds,
            AcceptedOrigins: new[]
            {
                "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "http://localhost:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            RpId: "auto",
            SupportedAlgs: new[] { -7 },
            UserVerification: "required");
    }

    private (bool registered, IReadOnlyList<string> credentialIds) ReadCredentialIds()
    {
        if (_credentialsFilePath is null || !File.Exists(_credentialsFilePath))
        {
            return (false, Array.Empty<string>());
        }

        try
        {
            using var stream = File.OpenRead(_credentialsFilePath);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (false, Array.Empty<string>());
            if (!doc.RootElement.TryGetProperty("credentials", out var credsElem)) return (false, Array.Empty<string>());
            if (credsElem.ValueKind != JsonValueKind.Array) return (false, Array.Empty<string>());

            var ids = new List<string>();
            foreach (var item in credsElem.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("credentialId", out var idElem)) continue;
                if (idElem.ValueKind != JsonValueKind.String) continue;
                var s = idElem.GetString();
                if (!string.IsNullOrWhiteSpace(s)) ids.Add(s);
            }
            return (ids.Count > 0, ids);
        }
        catch (Exception)
        {
            // Honest fallback. See class summary for rationale.
            return (false, Array.Empty<string>());
        }
    }
}
