namespace PAXCookbook.Broker.Native.Services;

// Stage 3i-C -- structural probe for an X.509 certificate present in
// a named Windows store (LocalMachine\My, CurrentUser\My, etc.).
// Used only by AuthProfileTestService for the structural "test"
// validation. Production wires WindowsCertificateProbe (X509Store
// open + thumbprint lookup); tests inject a fake that records
// (thumbprint, storeName) tuples and returns a canned verdict.
//
// Doctrine:
//   * The probe does not download CRLs, does not validate the chain,
//     does not check expiry. Stage 3i-C is structural ONLY -- it
//     confirms presence + thumbprint match. Live validation against
//     Microsoft endpoints is out of Stage 3i-C scope by design.
public interface ICertificateProbe
{
    // Returns true if a certificate with the given uppercase SHA-1
    // thumbprint is present in the named store. Returns false if the
    // store opens but no match is found. Throws when the store cannot
    // be opened (e.g. permission denied).
    bool Locate(string thumbprint, string store);
}
