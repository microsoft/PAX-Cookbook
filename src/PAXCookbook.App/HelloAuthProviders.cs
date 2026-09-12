using System.Text.Json;

namespace PAXCookbook.App;

// Windows Hello adapter for the provider-neutral session-unlock contract
// (T1-S1).
//
// This is the ONLY implemented provider after this slice. It wraps the existing
// browser-owned WebAuthn verification (WebAuthnService) unchanged and exposes
// only a neutral verdict on the shared contract. The Hello-specific failure
// response is kept OFF the neutral contract (LastFailure) so exact HTTP
// response parity is preserved by the owning route without leaking transport
// types into the contract.
internal sealed class HelloSessionUnlockProvider : ISessionUnlockProvider
{
    private readonly WebAuthnService _webAuthn;
    private readonly JsonElement _body;

    internal HelloSessionUnlockProvider(WebAuthnService webAuthn, JsonElement body)
    {
        _webAuthn = webAuthn;
        _body = body;
    }

    // Hello-specific failure detail from the last Authorize() call, or null on
    // approval. Not part of ISessionUnlockProvider; consumed only by the Hello
    // unlock route to reproduce its exact response.
    internal WebAuthnResponse? LastFailure { get; private set; }

    public string ProviderId => "windows-hello";

    public SessionUnlockOutcome Authorize()
    {
        LastFailure = _webAuthn.VerifyUnlockAssertion(_body);
        return LastFailure is null ? SessionUnlockOutcome.Grant : SessionUnlockOutcome.Deny;
    }
}
