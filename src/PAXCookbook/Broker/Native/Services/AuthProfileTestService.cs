using PAXCookbook.Broker.Native.Models;

namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-C -- structural test for /api/v1/auth/profiles/{id}/test.
// Confirms credential presence WITHOUT performing any HTTPS calls.
//
// Outcomes (mapped to AuthProfileTestResult.Detail):
//   structural_ok             -- mode-appropriate credential present.
//   secret_missing            -- CredMan reports the target absent.
//   cert_thumbprint_missing   -- row has no cert_thumbprint.
//   cert_not_found            -- thumbprint present but no match.
//   probe_failed              -- ICertificateProbe.Locate threw.
//   mode_unsupported          -- mode is neither Secret nor Cert
//                                (e.g. ManagedIdentity, deferred to
//                                Stage 3j+).
//
// On every outcome the row's last_verified_at + last_verified_result
// are best-effort updated (PS-broker parity); errors from the store
// update are swallowed (the client cares about the result envelope,
// not the side effect).
public sealed class AuthProfileTestService
{
    private readonly AuthProfileMutationStore _store;
    private readonly ICredentialSecretStore   _credStore;
    private readonly ICertificateProbe        _certProbe;
    private readonly Func<DateTimeOffset>     _utcNow;

    public AuthProfileTestService(
        AuthProfileMutationStore store,
        ICredentialSecretStore   credStore,
        ICertificateProbe        certProbe,
        Func<DateTimeOffset>?    utcNow = null)
    {
        _store     = store     ?? throw new ArgumentNullException(nameof(store));
        _credStore = credStore ?? throw new ArgumentNullException(nameof(credStore));
        _certProbe = certProbe ?? throw new ArgumentNullException(nameof(certProbe));
        _utcNow    = utcNow    ?? (() => DateTimeOffset.UtcNow);
    }

    public TestOutcome Test(string authProfileId)
    {
        var row = _store.GetById(authProfileId);
        if (row is null) return new TestOutcome.NotFound(authProfileId);

        var nowIso = FormatUtc(_utcNow());
        AuthProfileTestResult result;

        switch (row.Mode)
        {
            case AuthProfileModes.AppRegistrationSecret:
            {
                var present = _credStore.Exists(authProfileId);
                var ok      = present;
                var detail  = present ? "structural_ok" : "secret_missing";
                result = new AuthProfileTestResult(
                    AuthProfileId:      authProfileId,
                    Mode:               row.Mode,
                    ValidationKind:     "structural",
                    Ok:                 ok,
                    Detail:             detail,
                    LastVerifiedAt:     nowIso,
                    LastVerifiedResult: detail);
                break;
            }

            case AuthProfileModes.AppRegistrationCertificate:
            {
                if (string.IsNullOrEmpty(row.CertThumbprint))
                {
                    result = new AuthProfileTestResult(
                        AuthProfileId:      authProfileId,
                        Mode:               row.Mode,
                        ValidationKind:     "structural",
                        Ok:                 false,
                        Detail:             "cert_thumbprint_missing",
                        LastVerifiedAt:     nowIso,
                        LastVerifiedResult: "cert_thumbprint_missing");
                    break;
                }
                var storeSpec = string.IsNullOrEmpty(row.CertStore) ? AuthProfileModes.DefaultCertStore : row.CertStore;
                try
                {
                    var hit = _certProbe.Locate(row.CertThumbprint, storeSpec);
                    var detail = hit ? "structural_ok" : "cert_not_found";
                    result = new AuthProfileTestResult(
                        AuthProfileId:      authProfileId,
                        Mode:               row.Mode,
                        ValidationKind:     "structural",
                        Ok:                 hit,
                        Detail:             detail,
                        LastVerifiedAt:     nowIso,
                        LastVerifiedResult: detail);
                }
                catch (Exception)
                {
                    result = new AuthProfileTestResult(
                        AuthProfileId:      authProfileId,
                        Mode:               row.Mode,
                        ValidationKind:     "structural",
                        Ok:                 false,
                        Detail:             "probe_failed",
                        LastVerifiedAt:     nowIso,
                        LastVerifiedResult: "probe_failed");
                }
                break;
            }

            default:
                result = new AuthProfileTestResult(
                    AuthProfileId:      authProfileId,
                    Mode:               row.Mode,
                    ValidationKind:     "structural",
                    Ok:                 false,
                    Detail:             "mode_unsupported",
                    LastVerifiedAt:     nowIso,
                    LastVerifiedResult: "mode_unsupported");
                break;
        }

        // Best-effort persist last_verified_*. Failures (e.g. DB read-only
        // because the test is being run during a maintenance window) are
        // swallowed -- the client envelope is the source of truth.
        try
        {
            _store.SetLastVerified(authProfileId, nowIso, result.LastVerifiedResult);
        }
        catch
        {
            // intentional swallow -- structural test is best-effort persist.
        }

        return new TestOutcome.Tested(result);
    }

    private static string FormatUtc(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public abstract record TestOutcome
    {
        public sealed record Tested(AuthProfileTestResult Result) : TestOutcome;
        public sealed record NotFound(string AuthProfileId)       : TestOutcome;
    }
}
