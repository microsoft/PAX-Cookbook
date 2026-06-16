using System.Text.Json.Nodes;
using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-C -- POST / PUT / DELETE orchestration for /api/v1/auth/
// profiles. Re-auth is the FIRST gate at the route level (any 401
// reAuthRequired is emitted before this service is called); the
// service receives only validated bodies after the re-auth pass.
//
// The DELETE path auto-removes the credential bound by the secret
// route BEFORE deleting the row. The PS broker's
// Remove-AuthProfileRow does this best-effort -- if CredMan throws,
// the row is still deleted; the route logs and surfaces a
// `credentialDeleteFailed` field in the success envelope. The
// native broker mirrors this exact semantic via DeleteResult.
public sealed class AuthProfileMutationService
{
    private readonly AuthProfileMutationStore _store;
    private readonly ICredentialSecretStore   _credStore;
    private readonly Func<DateTimeOffset>     _utcNow;
    private readonly Func<string>             _newId;

    public AuthProfileMutationService(
        AuthProfileMutationStore store,
        ICredentialSecretStore   credStore,
        Func<DateTimeOffset>?    utcNow = null,
        Func<string>?            newId  = null)
    {
        _store     = store     ?? throw new ArgumentNullException(nameof(store));
        _credStore = credStore ?? throw new ArgumentNullException(nameof(credStore));
        _utcNow    = utcNow    ?? (() => DateTimeOffset.UtcNow);
        _newId     = newId     ?? (() => Guid.NewGuid().ToString("D").ToLowerInvariant());
    }

    public CreateOutcome Create(JsonNode? body)
    {
        var verdict = AuthProfileValidator.ValidateCreate(body);
        if (!verdict.Ok) return new CreateOutcome.Invalid(verdict.Errors);

        var req     = verdict.Create!;
        var nowIso  = FormatUtc(_utcNow());
        var newId   = _newId();

        // Reject duplicate name (PS broker emits 409 name_in_use).
        if (_store.NameInUse(req.Name, excludeProfileId: null))
            return new CreateOutcome.NameInUse(req.Name);

        // Stage the row. cred_man_target is server-stamped only when
        // mode = AppRegistrationSecret; otherwise null. The bind
        // route (POST /secret) is the one that WRITES CredMan; the
        // create route only declares the target name.
        string? credManTarget = null;
        if (req.Mode == AuthProfileModes.AppRegistrationSecret)
        {
            credManTarget = _credStore.ComposeTarget(newId);
        }

        var certStore = req.Mode == AuthProfileModes.AppRegistrationCertificate
            ? (string.IsNullOrEmpty(req.CertStore) ? AuthProfileModes.DefaultCertStore : req.CertStore)
            : (string?)null;

        var row = new AuthProfileRow(
            AuthProfileId:      newId,
            Name:               req.Name,
            Mode:               req.Mode,
            TenantId:           req.TenantId,
            ClientId:           req.ClientId,
            CredManTarget:      credManTarget,
            CertThumbprint:     req.Mode == AuthProfileModes.AppRegistrationCertificate ? req.CertThumbprint : null,
            CertStore:          certStore,
            Description:        req.Description,
            LastVerifiedAt:     null,
            LastVerifiedResult: null,
            CreatedAt:          nowIso,
            UpdatedAt:          nowIso);

        _store.Insert(row);
        return new CreateOutcome.Created(row);
    }

    public UpdateOutcome Update(string authProfileId, JsonNode? body)
    {
        var row = _store.GetById(authProfileId);
        if (row is null) return new UpdateOutcome.NotFound(authProfileId);

        var verdict = AuthProfileValidator.ValidateUpdate(body, row);
        if (!verdict.Ok) return new UpdateOutcome.Invalid(verdict.Errors);
        var req = verdict.Update!;

        // Reject name collision against another row.
        if (!string.Equals(req.Name, row.Name, StringComparison.Ordinal)
            && _store.NameInUse(req.Name!, excludeProfileId: authProfileId))
        {
            return new UpdateOutcome.NameInUse(req.Name!);
        }

        var nowIso = FormatUtc(_utcNow());
        var certStore = row.Mode == AuthProfileModes.AppRegistrationCertificate
            ? (string.IsNullOrEmpty(req.CertStore) ? AuthProfileModes.DefaultCertStore : req.CertStore)
            : (string?)null;

        var affected = _store.UpdateMutableFields(
            authProfileId:  authProfileId,
            name:           req.Name!,
            tenantId:       req.TenantId!,
            clientId:       req.ClientId!,
            description:    req.Description,
            certThumbprint: row.Mode == AuthProfileModes.AppRegistrationCertificate ? req.CertThumbprint : null,
            certStore:      certStore,
            updatedAt:      nowIso);
        if (affected != 1) return new UpdateOutcome.WriteFailed(authProfileId);

        var updated = _store.GetById(authProfileId)
            ?? throw new InvalidOperationException("auth profile vanished after update");
        return new UpdateOutcome.Updated(updated);
    }

    public DeleteOutcome Delete(string authProfileId)
    {
        var row = _store.GetById(authProfileId);
        if (row is null) return new DeleteOutcome.NotFound(authProfileId);

        // Best-effort credential delete. The PS broker's
        // Remove-AuthProfileRow swallows any CredMan exception and
        // proceeds with the row delete, recording credentialDeleteFailed
        // on the response envelope. The native broker mirrors that
        // semantic so a stale credential never blocks a profile delete.
        string? credentialDeleteFailed = null;
        try
        {
            if (row.Mode == AuthProfileModes.AppRegistrationSecret)
            {
                _credStore.Delete(authProfileId);
            }
        }
        catch (Exception ex)
        {
            credentialDeleteFailed = ex.Message;
        }

        var affected = _store.Delete(authProfileId);
        if (affected != 1)
            return new DeleteOutcome.WriteFailed(authProfileId);

        return new DeleteOutcome.Deleted(authProfileId, credentialDeleteFailed);
    }

    private static string FormatUtc(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    // ----------------- Outcome unions -----------------

    public abstract record CreateOutcome
    {
        public sealed record Created(AuthProfileRow Row) : CreateOutcome;
        public sealed record Invalid(IReadOnlyList<AuthProfileValidationError> Errors) : CreateOutcome;
        public sealed record NameInUse(string Name) : CreateOutcome;
    }

    public abstract record UpdateOutcome
    {
        public sealed record Updated(AuthProfileRow Row) : UpdateOutcome;
        public sealed record NotFound(string AuthProfileId) : UpdateOutcome;
        public sealed record Invalid(IReadOnlyList<AuthProfileValidationError> Errors) : UpdateOutcome;
        public sealed record NameInUse(string Name) : UpdateOutcome;
        public sealed record WriteFailed(string AuthProfileId) : UpdateOutcome;
    }

    public abstract record DeleteOutcome
    {
        public sealed record Deleted(string AuthProfileId, string? CredentialDeleteFailed) : DeleteOutcome;
        public sealed record NotFound(string AuthProfileId) : DeleteOutcome;
        public sealed record WriteFailed(string AuthProfileId) : DeleteOutcome;
    }
}
