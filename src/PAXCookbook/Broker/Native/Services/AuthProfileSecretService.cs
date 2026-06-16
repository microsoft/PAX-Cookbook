using System.Text.Json.Nodes;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-C -- POST + DELETE on /api/v1/auth/profiles/{id}/secret.
//
//   * POST writes the plaintext secret into Windows Credential
//     Manager at the canonical target name (PS broker calls this
//     Bind-AuthProfileSecret). The auth_profiles row's
//     cred_man_target column is set on success.
//   * DELETE removes the credential. Idempotent: missing target =>
//     200 OK with `removed:"absent"`.
//
// Both routes require mode = AppRegistrationSecret. AppRegistration
// Certificate and any future modes return 422
// auth_profile_mode_mismatch -- the secret family is meaningless for
// non-secret modes.
//
// Plaintext semantics: the bind route accepts the secret in a JSON
// body field. The service hands it to ICredentialSecretStore.Write
// (production zero-fills the buffer in the WindowsCredentialSecret
// Store implementation). The clientSecret string is NEVER logged
// and NEVER written into the auth_profiles table.
public sealed class AuthProfileSecretService
{
    public const int MaxSecretLength = 2048;

    private readonly AuthProfileMutationStore _store;
    private readonly ICredentialSecretStore   _credStore;
    private readonly Func<DateTimeOffset>     _utcNow;

    public AuthProfileSecretService(
        AuthProfileMutationStore store,
        ICredentialSecretStore   credStore,
        Func<DateTimeOffset>?    utcNow = null)
    {
        _store     = store     ?? throw new ArgumentNullException(nameof(store));
        _credStore = credStore ?? throw new ArgumentNullException(nameof(credStore));
        _utcNow    = utcNow    ?? (() => DateTimeOffset.UtcNow);
    }

    public BindOutcome Bind(string authProfileId, JsonNode? body)
    {
        var row = _store.GetById(authProfileId);
        if (row is null) return new BindOutcome.NotFound(authProfileId);
        if (row.Mode != AuthProfileModes.AppRegistrationSecret)
            return new BindOutcome.ModeMismatch(authProfileId, row.Mode);

        if (body is not JsonObject obj)
            return new BindOutcome.InvalidJson();
        if (!obj.TryGetPropertyValue("clientSecret", out var s) || s is null)
            return new BindOutcome.SecretRequired();
        if (s.GetValueKind() != System.Text.Json.JsonValueKind.String)
            return new BindOutcome.SecretRequired();
        var secret = s.GetValue<string>();
        if (string.IsNullOrEmpty(secret))
            return new BindOutcome.SecretRequired();
        if (secret.Length > MaxSecretLength)
            return new BindOutcome.SecretTooLong(secret.Length);

        try
        {
            _credStore.Write(authProfileId, secret);
        }
        catch (Exception ex)
        {
            return new BindOutcome.WriteFailed(ex.Message);
        }

        var nowIso = FormatUtc(_utcNow());
        var target = _credStore.ComposeTarget(authProfileId);
        _store.SetCredManTarget(authProfileId, target, nowIso);
        return new BindOutcome.Bound(authProfileId, target);
    }

    public RemoveOutcome Remove(string authProfileId)
    {
        var row = _store.GetById(authProfileId);
        if (row is null) return new RemoveOutcome.NotFound(authProfileId);
        if (row.Mode != AuthProfileModes.AppRegistrationSecret)
            return new RemoveOutcome.ModeMismatch(authProfileId, row.Mode);

        var existed = _credStore.Exists(authProfileId);
        try
        {
            _credStore.Delete(authProfileId);
        }
        catch (Exception ex)
        {
            return new RemoveOutcome.WriteFailed(ex.Message);
        }

        var nowIso = FormatUtc(_utcNow());
        _store.SetCredManTarget(authProfileId, null, nowIso);
        return new RemoveOutcome.Removed(authProfileId, existed);
    }

    private static string FormatUtc(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public abstract record BindOutcome
    {
        public sealed record Bound(string AuthProfileId, string CredManTarget) : BindOutcome;
        public sealed record NotFound(string AuthProfileId) : BindOutcome;
        public sealed record ModeMismatch(string AuthProfileId, string CurrentMode) : BindOutcome;
        public sealed record InvalidJson : BindOutcome;
        public sealed record SecretRequired : BindOutcome;
        public sealed record SecretTooLong(int Length) : BindOutcome;
        public sealed record WriteFailed(string Detail) : BindOutcome;
    }

    public abstract record RemoveOutcome
    {
        public sealed record Removed(string AuthProfileId, bool Existed) : RemoveOutcome;
        public sealed record NotFound(string AuthProfileId) : RemoveOutcome;
        public sealed record ModeMismatch(string AuthProfileId, string CurrentMode) : RemoveOutcome;
        public sealed record WriteFailed(string Detail) : RemoveOutcome;
    }
}
