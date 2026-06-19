using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PAXCookbook.App;

// Browser-owned WebAuthn foundation — parity port of the PowerShell oracle
// (app\broker\Routes\BrokerWebAuthn.ps1 + app\broker\Auth\WebAuthnVerify.ps1).
//
// Doctrine (preserved verbatim from the oracle):
//   - Cookbook NEVER collects, hashes, proxies, or stores any password, PIN,
//     or biometric. The unlock ceremony is performed by the browser's own
//     WebAuthn stack; the broker only mints challenges and verifies
//     assertions. Attestation is NOT verified (the local browser process is
//     trusted); the SPA pre-converts the COSE public key to SPKI so the
//     broker performs a plain ECDsa.ImportSubjectPublicKeyInfo — no CBOR.
//   - ES256 (alg -7) only. WebAuthn assertion signatures are DER-encoded, so
//     verification uses DSASignatureFormat.Rfc3279DerSequence (NOT raw P1363).
//   - Challenges are single-use, purpose-tagged, and expire after 300s.
//   - This native runtime does NOT use broker-owned WinRT Windows Hello
//     (UserConsentVerifier). The legacy POST /unlock path is intentionally a
//     bounded honest not-implemented response (see Program.cs); it never
//     fabricates a verified verdict.
internal sealed class WebAuthnService
{
    private const int ChallengeTtlSeconds = 300;
    private const int Es256 = -7;

    private readonly string _credentialsFile;
    private readonly string[] _acceptedOrigins;

    private readonly object _challengeGate = new();
    private readonly Dictionary<string, PendingChallenge> _pendingChallenges = new(StringComparer.Ordinal);

    private readonly object _storeGate = new();

    private static readonly JsonSerializerOptions StoreJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    internal WebAuthnService(string workspacePath, int port)
    {
        string authDir = Path.Combine(workspacePath, "Auth");
        Directory.CreateDirectory(authDir);
        _credentialsFile = Path.Combine(authDir, "webauthn-credentials.json");
        _acceptedOrigins = new[]
        {
            $"http://127.0.0.1:{port}",
            $"http://localhost:{port}",
        };
    }

    // GET /api/v1/broker/webauthn/status (lock-bypass).
    internal WebAuthnResponse GetStatus()
    {
        CredentialStore store = LoadCredentials();
        string[] ids = store.credentials.Select(c => c.credentialId).ToArray();
        return new WebAuthnResponse(200, new
        {
            registered = ids.Length > 0,
            credentialIds = ids,
            acceptedOrigins = _acceptedOrigins,
            rpId = "auto",
            supportedAlgs = new[] { Es256 },
            userVerification = "required",
        });
    }

    // POST /api/v1/broker/webauthn/unlock-challenge (lock-bypass).
    internal WebAuthnResponse NewUnlockChallenge()
    {
        string challenge = MintChallenge("unlock");
        return new WebAuthnResponse(200, new
        {
            challenge,
            timeoutMs = 60000,
            userVerification = "required",
        });
    }

    // POST /api/v1/broker/webauthn/bootstrap-register-challenge (lock-bypass).
    // First-run passkey enrolment: mints a creation challenge and the relying-
    // party / user material the browser needs for navigator.credentials.create.
    internal WebAuthnResponse NewBootstrapRegisterChallenge()
    {
        string challenge = MintChallenge("unlock_register");
        string userName = Environment.UserName;
        if (string.IsNullOrWhiteSpace(userName))
        {
            userName = "cookbook";
        }

        return new WebAuthnResponse(200, new
        {
            challenge,
            timeoutMs = 60000,
            userVerification = "required",
            rpName = "PAX Cookbook",
            userIdBase64Url = B64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            userName = "cookbook-" + userName,
            userDisplayName = userName,
            pubKeyCredParams = new[]
            {
                new { type = "public-key", alg = Es256 },
            },
            acceptedOrigins = _acceptedOrigins,
        });
    }

    // POST /api/v1/broker/webauthn/unlock (lock-bypass). Verifies an ES256
    // assertion against a previously registered credential. On success the
    // broker lock transitions to Unlocked. There is NO fast-path that unlocks
    // without a verified signature.
    internal WebAuthnResponse Unlock(JsonElement? body)
    {
        if (body is null)
        {
            return new WebAuthnResponse(400, new { error = "invalid_json" });
        }

        WebAuthnResponse? failure = VerifyAssertion(body.Value, "unlock", out _);
        if (failure is not null)
        {
            return failure;
        }

        BrokerLock.SetUnlocked();
        return new WebAuthnResponse(200, BuildUnlockSuccessBody());
    }

    // POST /api/v1/broker/reauth/manual-cook/challenge (X16B; NOT lock-bypass).
    // Mints a purpose-tagged single-use challenge for a manual-cook step-up.
    // The challenge is opaque random bytes; the operation it authorizes is
    // bound by the purpose tag here and by the recipeId supplied at verify time.
    internal WebAuthnResponse NewManualCookChallenge()
    {
        string challenge = MintChallenge("manual_cook");
        return new WebAuthnResponse(200, new
        {
            challenge,
            timeoutMs = 60000,
            userVerification = "required",
            opClass = "manualCook",
            challengeTtlSeconds = ChallengeTtlSeconds,
        });
    }

    // POST /api/v1/broker/reauth/manual-cook/verify (X16B; NOT lock-bypass).
    // Verifies an ES256 assertion against a registered credential exactly as the
    // unlock ceremony does, but instead of transitioning the broker lock it
    // grants a single-use, recipe-bound, lock-generation-bound in-memory
    // authorization for ONE manual cook of the named recipe. It never unlocks
    // the broker and never fabricates a verified verdict.
    internal WebAuthnResponse VerifyManualCook(JsonElement? body)
    {
        if (body is null)
        {
            return new WebAuthnResponse(400, new { error = "invalid_json" });
        }

        JsonElement b = body.Value;
        string? recipeId = GetStr(b, "recipeId");
        if (IsBlank(recipeId))
        {
            return new WebAuthnResponse(400, new
            {
                error = "missing_fields",
                reason = "recipeId_required",
            });
        }

        WebAuthnResponse? failure = VerifyAssertion(b, "manual_cook", out _);
        if (failure is not null)
        {
            return failure;
        }

        // The bake's Windows Hello step-up doubles as the session unlock: a
        // verified assertion proves presence, so it lifts the inactivity lock
        // (when it engaged) and refreshes the activity anchor before the grant is
        // recorded. SetUnlocked does not bump the lock generation, so the grant
        // below is captured against the same generation the cook route reads when
        // it consumes it. This keeps a manual bake to ONE Windows Hello prompt
        // that both authorizes the cook and refreshes the session, instead of a
        // locked session blocking the bake before it can reach its own step-up.
        BrokerLock.SetUnlocked();
        ManualCookReAuth.Grant(recipeId!, BrokerLock.CurrentLockGeneration);
        return new WebAuthnResponse(200, new
        {
            ok = true,
            opClass = "manualCook",
            recipeId,
            verificationResult = "Verified",
            verificationPath = "webauthn",
            authorizationTtlSeconds = ManualCookReAuth.AuthorizationTtlSeconds,
        });
    }

    // Shared ES256 assertion-verification core for the unlock ceremony and the
    // manual-cook step-up. Validates the supplied assertion against a single-use
    // purpose-tagged challenge and a registered credential. Returns null and
    // the verified credential on success (after bumping signCount/lastUsedUtc);
    // otherwise returns the failure response and leaves verifiedCred null. This
    // method performs NO lock transition and NO authorization grant — those are
    // the caller's responsibility so each entry point owns its own side effect.
    private WebAuthnResponse? VerifyAssertion(JsonElement b, string expectedPurpose, out StoredCredential? verifiedCred)
    {
        verifiedCred = null;

        string? credentialId = GetStr(b, "credentialId");
        string? clientDataJSON = GetStr(b, "clientDataJSON");
        string? authenticatorData = GetStr(b, "authenticatorData");
        string? signature = GetStr(b, "signature");
        string? challenge = GetStr(b, "challenge");

        if (IsBlank(credentialId) || IsBlank(clientDataJSON) || IsBlank(authenticatorData) ||
            IsBlank(signature) || IsBlank(challenge))
        {
            return new WebAuthnResponse(400, new { error = "missing_fields" });
        }

        if (!ConfirmChallenge(challenge!, expectedPurpose))
        {
            return new WebAuthnResponse(400, new
            {
                error = "challenge_invalid",
                reason = "challenge_unknown_or_expired",
            });
        }

        CredentialStore store = LoadCredentials();
        StoredCredential? cred = store.credentials
            .FirstOrDefault(c => string.Equals(c.credentialId, credentialId, StringComparison.Ordinal));
        if (cred is null)
        {
            return VerificationFailed("credential_unknown");
        }

        if (cred.alg != Es256)
        {
            return VerificationFailed("credential_alg_unsupported");
        }

        byte[] clientDataBytes;
        byte[] authDataBytes;
        byte[] signatureBytes;
        byte[] spkiBytes;
        try
        {
            clientDataBytes = B64UrlDecode(clientDataJSON!);
            authDataBytes = B64UrlDecode(authenticatorData!);
            signatureBytes = B64UrlDecode(signature!);
        }
        catch
        {
            return VerificationFailed("b64_decode_failed");
        }

        try
        {
            spkiBytes = Convert.FromBase64String(cred.publicKeySpkiBase64);
        }
        catch
        {
            return VerificationFailed("public_key_import_failed");
        }

        string? clientDataReason = VerifyClientData(clientDataBytes, "webauthn.get", challenge!);
        if (clientDataReason is not null)
        {
            return VerificationFailed(clientDataReason);
        }

        string? authDataReason = VerifyAuthenticatorFlags(authDataBytes);
        if (authDataReason is not null)
        {
            return VerificationFailed(authDataReason);
        }

        bool verified;
        try
        {
            using ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spkiBytes, out _);
            byte[] clientDataHash = SHA256.HashData(clientDataBytes);
            byte[] signedBytes = new byte[authDataBytes.Length + clientDataHash.Length];
            Buffer.BlockCopy(authDataBytes, 0, signedBytes, 0, authDataBytes.Length);
            Buffer.BlockCopy(clientDataHash, 0, signedBytes, authDataBytes.Length, clientDataHash.Length);

            verified = ecdsa.VerifyData(
                signedBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch
        {
            return VerificationFailed("public_key_import_failed");
        }

        if (!verified)
        {
            return VerificationFailed("signature_verification_failed");
        }

        cred.signCount += 1;
        cred.lastUsedUtc = DateTime.UtcNow.ToString("o");
        SaveCredentials(store);

        verifiedCred = cred;
        return null;
    }

    // POST /api/v1/broker/webauthn/bootstrap-register-unlock (lock-bypass).
    // First-run combined enrol + unlock. Ordered validation gates; any failure
    // returns a structured rejection tagged with the stage that failed.
    internal WebAuthnResponse BootstrapRegisterUnlock(JsonElement? body)
    {
        string attemptId = B64UrlEncode(RandomNumberGenerator.GetBytes(6));

        if (body is null)
        {
            return Reject("parse_request", "invalid_json", "Request body was empty or not valid JSON.", attemptId);
        }

        JsonElement b = body.Value;
        string? credentialId = GetStr(b, "credentialId");
        string? publicKeySpki = GetStr(b, "publicKeySpki");
        string? clientDataJSON = GetStr(b, "clientDataJSON");
        string? authenticatorData = GetStr(b, "authenticatorData");
        string? challenge = GetStr(b, "challenge");
        int? alg = GetInt(b, "alg");
        string? label = GetStr(b, "label");

        if (IsBlank(credentialId) || IsBlank(publicKeySpki) || IsBlank(clientDataJSON) ||
            IsBlank(authenticatorData) || IsBlank(challenge) || alg is null)
        {
            return Reject("parse_request", "missing_fields",
                "One or more required registration fields are missing.", attemptId);
        }

        if (!ConfirmChallenge(challenge!, "unlock_register"))
        {
            return Reject("challenge", "challenge_invalid",
                "The registration challenge is unknown or has expired.", attemptId);
        }

        byte[] clientDataBytes;
        try
        {
            clientDataBytes = B64UrlDecode(clientDataJSON!);
        }
        catch
        {
            return Reject("client_data", "client_data_decode_failed",
                "clientDataJSON could not be base64url-decoded.", attemptId);
        }

        JsonElement clientData;
        try
        {
            using var doc = JsonDocument.Parse(clientDataBytes);
            clientData = doc.RootElement.Clone();
        }
        catch
        {
            return Reject("client_data", "client_data_parse_failed",
                "clientDataJSON was not valid JSON.", attemptId);
        }

        string? cdType = GetStr(clientData, "type");
        if (!string.Equals(cdType, "webauthn.create", StringComparison.Ordinal))
        {
            return Reject("client_data", "client_data_type_mismatch",
                "clientData.type was not 'webauthn.create'.", attemptId);
        }

        string? cdChallenge = GetStr(clientData, "challenge");
        if (!string.Equals(cdChallenge, challenge, StringComparison.Ordinal))
        {
            return Reject("challenge", "client_data_challenge_mismatch",
                "clientData.challenge did not match the issued challenge.", attemptId);
        }

        string? cdOrigin = GetStr(clientData, "origin");
        if (cdOrigin is null || !OriginAcceptable(cdOrigin))
        {
            return Reject("origin", "client_data_origin_unacceptable",
                "clientData.origin is not an accepted loopback origin.", attemptId);
        }

        byte[] authDataBytes;
        try
        {
            authDataBytes = B64UrlDecode(authenticatorData!);
        }
        catch
        {
            return Reject("auth_data", "authenticator_data_decode_failed",
                "authenticatorData could not be base64url-decoded.", attemptId);
        }

        if (authDataBytes.Length < 37)
        {
            return Reject("auth_data", "authenticator_data_too_short",
                "authenticatorData is shorter than the 37-byte minimum.", attemptId);
        }

        byte flags = authDataBytes[32];
        if ((flags & 0x01) == 0)
        {
            return Reject("flags", "user_presence_not_asserted",
                "The user-presence (UP) flag was not set.", attemptId);
        }
        if ((flags & 0x04) == 0)
        {
            return Reject("flags", "user_verification_not_asserted",
                "The user-verification (UV) flag was not set.", attemptId);
        }

        byte[] spkiBytes;
        try
        {
            spkiBytes = Convert.FromBase64String(publicKeySpki!);
        }
        catch
        {
            return Reject("public_key", "public_key_decode_failed",
                "publicKeySpki could not be base64-decoded.", attemptId);
        }

        try
        {
            using ECDsa ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(spkiBytes, out _);
            if (ecdsa.KeySize != 256)
            {
                return Reject("public_key", "public_key_not_p256",
                    "The supplied SPKI public key is not a P-256 key.", attemptId);
            }
        }
        catch
        {
            return Reject("public_key", "public_key_import_failed",
                "publicKeySpki did not import as a valid SPKI public key.", attemptId);
        }

        if (alg != Es256)
        {
            return Reject("alg", "alg_unsupported",
                "Only ES256 (alg -7) is supported.", attemptId);
        }

        CredentialStore store = LoadCredentials();
        if (store.credentials.Any(c => string.Equals(c.credentialId, credentialId, StringComparison.Ordinal)))
        {
            return Reject("credential_id", "credential_already_registered",
                "A credential with this credentialId is already registered.", attemptId);
        }

        try
        {
            string nowIso = DateTime.UtcNow.ToString("o");
            store.credentials.Add(new StoredCredential
            {
                credentialId = credentialId!,
                publicKeySpkiBase64 = publicKeySpki!,
                alg = Es256,
                createdUtc = nowIso,
                lastUsedUtc = nowIso,
                signCount = 0,
                label = IsBlank(label) ? null : label,
            });
            SaveCredentials(store);
        }
        catch
        {
            return Reject("credential_store", "credential_persist_failed",
                "The credential could not be persisted to the credential store.", attemptId);
        }

        BrokerLock.SetUnlocked();

        LockSnapshot snap = BrokerLock.GetSnapshot();
        return new WebAuthnResponse(200, new
        {
            ok = true,
            registered = true,
            credentialId,
            state = snap.state,
            lastActivityUtc = snap.lastActivityUtc,
            inactivityTimeoutMinutes = snap.inactivityTimeoutMinutes,
            inactivityRemainingSeconds = snap.inactivityRemainingSeconds,
            verificationResult = "Verified",
            verificationPath = "webauthn",
            attemptId,
        });
    }

    private object BuildUnlockSuccessBody()
    {
        LockSnapshot snap = BrokerLock.GetSnapshot();
        return new
        {
            ok = true,
            state = snap.state,
            lastActivityUtc = snap.lastActivityUtc,
            inactivityTimeoutMinutes = snap.inactivityTimeoutMinutes,
            inactivityRemainingSeconds = snap.inactivityRemainingSeconds,
            verificationResult = "Verified",
            verificationPath = "webauthn",
        };
    }

    // Parses clientDataJSON for an assertion (webauthn.get). Returns null on
    // success, or an oracle reason tag on failure.
    private string? VerifyClientData(byte[] clientDataBytes, string expectedType, string expectedChallenge)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(clientDataBytes);
            root = doc.RootElement.Clone();
        }
        catch
        {
            return "client_data_parse_failed";
        }

        string? type = GetStr(root, "type");
        if (!string.Equals(type, expectedType, StringComparison.Ordinal))
        {
            return "client_data_type_mismatch";
        }

        string? challenge = GetStr(root, "challenge");
        if (!string.Equals(challenge, expectedChallenge, StringComparison.Ordinal))
        {
            return "client_data_challenge_mismatch";
        }

        string? origin = GetStr(root, "origin");
        if (origin is null || !OriginAcceptable(origin))
        {
            return "client_data_origin_unacceptable";
        }

        return null;
    }

    // Returns null when UP+UV flags are present on a sufficiently long
    // authenticatorData buffer, else the oracle reason tag.
    private static string? VerifyAuthenticatorFlags(byte[] authDataBytes)
    {
        if (authDataBytes.Length < 37)
        {
            return "authenticator_data_too_short";
        }

        byte flags = authDataBytes[32];
        if ((flags & 0x01) == 0)
        {
            return "user_presence_not_asserted";
        }
        if ((flags & 0x04) == 0)
        {
            return "user_verification_not_asserted";
        }

        return null;
    }

    private bool OriginAcceptable(string origin)
    {
        foreach (string accepted in _acceptedOrigins)
        {
            if (string.Equals(origin, accepted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static WebAuthnResponse VerificationFailed(string reason)
    {
        return new WebAuthnResponse(401, new
        {
            error = "webauthn_verification_failed",
            reason,
        });
    }

    private static WebAuthnResponse Reject(string stage, string code, string message, string attemptId)
    {
        return new WebAuthnResponse(400, new
        {
            ok = false,
            code,
            message,
            stage,
            attemptId,
        });
    }

    // --- Challenge store -----------------------------------------------------

    private string MintChallenge(string purpose)
    {
        string challenge = B64UrlEncode(RandomNumberGenerator.GetBytes(32));
        lock (_challengeGate)
        {
            SweepChallenges();
            _pendingChallenges[challenge] = new PendingChallenge(purpose, DateTime.UtcNow);
        }

        return challenge;
    }

    private bool ConfirmChallenge(string challenge, string expectedPurpose)
    {
        lock (_challengeGate)
        {
            SweepChallenges();
            if (!_pendingChallenges.TryGetValue(challenge, out PendingChallenge pending))
            {
                return false;
            }

            // Single-use: remove regardless of purpose match so a cross-purpose
            // confirm cannot be replayed against the correct purpose later.
            _pendingChallenges.Remove(challenge);

            if (!string.Equals(pending.Purpose, expectedPurpose, StringComparison.Ordinal))
            {
                return false;
            }

            if ((DateTime.UtcNow - pending.CreatedUtc).TotalSeconds > ChallengeTtlSeconds)
            {
                return false;
            }

            return true;
        }
    }

    // Caller holds _challengeGate.
    private void SweepChallenges()
    {
        DateTime now = DateTime.UtcNow;
        var expired = new List<string>();
        foreach (var kvp in _pendingChallenges)
        {
            if ((now - kvp.Value.CreatedUtc).TotalSeconds > ChallengeTtlSeconds)
            {
                expired.Add(kvp.Key);
            }
        }

        foreach (string key in expired)
        {
            _pendingChallenges.Remove(key);
        }
    }

    // --- Credential store ----------------------------------------------------

    private CredentialStore LoadCredentials()
    {
        lock (_storeGate)
        {
            if (!File.Exists(_credentialsFile))
            {
                return new CredentialStore();
            }

            try
            {
                string json = File.ReadAllText(_credentialsFile);
                CredentialStore? store = JsonSerializer.Deserialize<CredentialStore>(json, StoreJsonOptions);
                if (store is null)
                {
                    return new CredentialStore();
                }

                store.credentials ??= new List<StoredCredential>();
                return store;
            }
            catch
            {
                return new CredentialStore();
            }
        }
    }

    private void SaveCredentials(CredentialStore store)
    {
        lock (_storeGate)
        {
            store.schemaVersion = 1;
            string json = JsonSerializer.Serialize(store, StoreJsonOptions);
            string tempFile = _credentialsFile + ".tmp";
            File.WriteAllText(tempFile, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempFile, _credentialsFile, overwrite: true);
        }
    }

    // --- Helpers -------------------------------------------------------------

    private static bool IsBlank(string? s) => string.IsNullOrWhiteSpace(s);

    private static string? GetStr(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static int? GetInt(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string B64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] B64UrlDecode(string value)
    {
        string s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }

    private readonly record struct PendingChallenge(string Purpose, DateTime CreatedUtc);
}

// Result of a WebAuthn route handler: HTTP status + JSON-serializable body.
internal sealed record WebAuthnResponse(int Status, object Body);

// Persisted credential record (camelCase to match the oracle JSON contract at
// <workspace>\Auth\webauthn-credentials.json).
internal sealed class StoredCredential
{
    public string credentialId { get; set; } = string.Empty;
    public string publicKeySpkiBase64 { get; set; } = string.Empty;
    public int alg { get; set; } = -7;
    public string createdUtc { get; set; } = string.Empty;
    public string lastUsedUtc { get; set; } = string.Empty;
    public long signCount { get; set; }
    public string? label { get; set; }
}

internal sealed class CredentialStore
{
    public int schemaVersion { get; set; } = 1;
    public List<StoredCredential> credentials { get; set; } = new();
}
