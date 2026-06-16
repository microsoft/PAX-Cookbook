# Release signing -- current reality and the path forward

This document describes how PAX Cookbook release packages are
authenticated **today** and how the trust chain has evolved.
It is meant for two audiences:

  - **Chefs** -- people running the Cookbook on a Windows laptop
    and deciding whether to apply a downloaded update package.
    A chef manages the workspace's local trust allowlist.
  - **Distributors** (a.k.a. release authors) -- people producing
    the manifest and the `.zip` package that chefs will download.
    A distributor holds the release-signing key and publishes the
    manifest URL.

In a self-distribution deployment (one human signs and runs their
own release) the same person plays both roles — but the two
responsibilities remain distinct in this document.

As of Phase R, the Cookbook performs **real cryptographic verification**
of detached signatures using RSA-PKCS1v15-SHA256. Earlier slices were
intentionally honest about *not* performing crypto; Phase R replaces
that absence with a deterministic verifier. Everything in the
"Today" section is honest about what the shipping code does.

---

## Today (current reality, post Phase R)

### What the Cookbook actually attests

When the Settings UI's **Check for updates** button runs, the broker:

  1. Issues exactly one HTTPS GET to the configured update-manifest
     URL (and only that URL -- no redirects followed, no CDN fan-out).
  2. Validates the returned JSON against a closed schema. Unknown
     fields are hard-rejected.
  3. Captures the validated manifest in memory (the "manifest
     snapshot") and surfaces the version delta to the chef.

When the chef then presses **Download update**, the broker:

  1. Reads `latestCookbook.packageUrl` and `latestCookbook.sha256`
     from the snapshot.
  2. Issues exactly one HTTPS GET to that package URL, streaming the
     body to a `.partial` file under `<workspace>\Updates\`.
  3. Recomputes the SHA-256 of the downloaded bytes and compares it
     to the manifest's declared hash. A mismatch causes the partial
     file to be deleted; the staged package only exists if the hash
     matches.
  4. Writes a sidecar `<filename>.zip.metadata.json` capturing the
     verified hash, the source URL, the manifest snapshot, and the
     download timestamp.

After staging, **for every package** the trust evaluator
(`app/broker/Update/Trust.psm1`):

  5. Recomputes the SHA-256 again and confirms `hashMatches`.
  6. If a `<filename>.zip.sig` envelope is present, the verifier in
     `app/broker/Update/Signature.psm1` decodes the embedded signer
     certificate, checks that its own SHA-1 thumbprint matches the
     envelope's `signerCertThumbprint`, and uses the cert's RSA
     public key to verify the signature over the package hash.
  7. If verification succeeded, the verifier matches the signer
     thumbprint against `<workspace>\Trust\trusted-signers.json`. A
     match emits `overallStatus = 'verified'` and `signatureVerified
     = $true`. Any other outcome emits a truthful sub-state and
     `signatureVerified = $false`.

The integrity attestations this build can therefore make are:

  - *Hash-matched*: the bytes on disk match the SHA-256 the manifest
    said they would have at staging time.
  - *Hash-matched + signature verified*: the bytes are additionally
    signed by an RSA private key whose corresponding public key is
    embedded in the `.sig`, and the cert's thumbprint matches one in
    the chef's allowlist.

### How a chef verifies a release today

To verify a release outside the Cookbook UI -- for example before
running an installer -- compute the SHA-256 manually and compare it
to the value published in the release notes:

```powershell
Get-FileHash -Algorithm SHA256 `
    -LiteralPath "$Env:USERPROFILE\PAX Cookbook\Updates\<filename>.zip" |
    Format-List Hash, Path
```

The hash should match the value in the manifest (which the broker
already verified) **and** the value in the human-readable release
notes (which only a human can compare).

### What is *not* claimed today

The Settings UI surfaces per-package trust state. Read every label
literally:

  - `Signature verified (trusted signer)` -- the package hash matched,
    the detached signature was cryptographically verified against the
    embedded cert's RSA public key, AND the cert's SHA-1 thumbprint
    appears in the chef's `trusted-signers.json` allowlist. This
    is the only label that emits `signatureVerified = true`.
  - `Unsigned (hash verified)` -- the SHA-256 sidecar matches the
    bytes on disk. There is no signature on disk.
  - `Hash mismatch` -- the bytes on disk no longer match the hash
    captured at staging time. Either tampering or disk corruption.
    Do not apply this package.
  - `Hash unknown` -- the sidecar metadata is missing or unreadable.
    The Cookbook cannot make any attestation about this file.
  - `Signature invalid` -- a `.sig` envelope exists but failed real
    cryptographic verification. Possible causes: tampering with the
    package, tampering with the `.sig`, envelope schema violation,
    or the embedded cert's thumbprint disagreeing with its bytes.
    Do not apply this package.
  - `Signature on disk (verifier unavailable)` -- a `.sig` sidecar
    exists but the appliance-side verifier module is missing. The
    Cookbook cannot make a cryptographic claim in this state.
  - `Signer not in allowlist` -- the signature verified
    cryptographically, but the signer's certificate thumbprint does
    not appear in `<workspace>\Trust\trusted-signers.json`. The
    package is hash-verified and signature-valid but the *identity*
    is untrusted.
  - `Signer metadata invalid` -- the `.signer.json` sidecar (UI
    metadata, distinct from the cryptographic `.sig`) is missing
    required fields or carries unknown fields.

The Phase R verifier never emits `signatureVerified = true` outside
the `verified` overall status. This is enforced by a defense-in-
depth conjunction in `Trust.psm1` that re-derives the field from
`hashMatches AND signaturePresent AND signatureValid AND signerKnown
AND overallStatus == 'verified'`. A future edit to the decision
tree cannot accidentally escalate the field.

---

## Future (planned workflow)

The artifact layout below is already enforced by the broker's
read-only trust evaluator (`app/broker/Update/Trust.psm1`). Files
that match the layout are inspected and reported on; files that
don't are ignored. **None of these files are produced by the broker
itself; they are all release-time artifacts.**

### Per-package sidecars

Located next to the staged package in `<workspace>\Updates\`:

```
<filename>.zip                       package
<filename>.zip.metadata.json         broker-produced (Phase O)
<filename>.zip.sha256                optional, sha256sum-format text
<filename>.zip.sig                   optional, future detached signature
<filename>.zip.signer.json           optional, future signer attestation
```

The `.sha256` file uses the standard `sha256sum` layout
(`<HEX>  <filename>`) and is purely a convenience for external
tooling -- the same hash is already captured authoritatively inside
`.metadata.json`.

The `.sig` file is a UTF-8 JSON envelope with a closed schema.
Unknown keys are hard-rejected by `Signature.psm1`:

```jsonc
{
  "schemaVersion":        1,
  "packageFile":          "pax-cookbook-1.0.0.zip",
  "packageSha256":        "<UPPERCASE 64-hex>",
  "hashAlgorithm":        "SHA256",
  "signatureAlgorithm":   "RSA-PKCS1v15-SHA256",
  "signatureBase64":      "<base64 of raw signature bytes>",
  "signerCertBase64":     "<base64 of DER-encoded signer cert>",
  "signerCertThumbprint": "<UPPERCASE 40-hex SHA-1 thumbprint>",
  "signedAtUtc":          "<ISO-8601 UTC>"
}
```

The choice of RSA-PKCS1v15-SHA256 is deliberate: it is universally
supported by .NET's `RSACertificateExtensions.GetRSAPublicKey` /
`RSA.VerifyHash`, has no padding-mode ambiguity at verification time,
and produces deterministic output for a given (key, hash) pair.
A different algorithm (RSA-PSS, ECDSA, Ed25519) would require both a
new `signatureAlgorithm` enum value AND a new envelope `schemaVersion`.

### `<filename>.zip.signer.json` schema

```jsonc
{
  "schemaVersion": 1,
  "signerName":         "Aggregated Copilot Analytics Team",
  "signerEmail":        "team@example.invalid",
  "certThumbprint":     "<40 uppercase hex chars; SHA-1 thumbprint>",
  "certSubject":        "CN=...",
  "signedAtUtc":        "2026-...",
  "signatureAlgorithm": "PKCS7-RSA-SHA256-detached",
  "signatureFile":      "<filename>.zip.sig"
}
```

Strict schema. Unknown fields are hard-rejected; missing required
fields cause the file to be reported as `signerMetadataInvalid`.

### `<workspace>\Trust\trusted-signers.json` (chef allowlist)

```jsonc
{
  "schemaVersion": 1,
  "signers": [
    {
      "name":           "Aggregated Copilot Analytics Team",
      "certThumbprint": "<40 uppercase hex chars; SHA-1 thumbprint>",
      "addedAtUtc":     "2026-...",
      "notes":          "Pin for production releases."
    }
  ]
}
```

The allowlist is **chef-managed** and is never bundled with the
Cookbook install tree. The default state is "file does not exist";
in that state the trust evaluator reports `signaturePresent` packages
as `signaturePresentNotVerified` rather than as `signerKnown`,
because there is no identity to match against.

### Release-signing checklist (Phase R)

When producing a signed release, the distributor runs:

```powershell
# 1. Build the package deterministically.
pwsh -File .\tools\release\Build-Release.ps1 `
    -SourceEpoch ([datetime]::new(2026,1,1,0,0,0,[DateTimeKind]::Utc))

# 2. Sign the package with a distributor-managed certificate.
pwsh -File .\tools\release\Sign-Release.ps1 `
    -PackagePath dist\stable\pax-cookbook-1.0.0\pax-cookbook-1.0.0.zip `
    -CertificateThumbprint <40-hex from CurrentUser\My> `
    -SignerName 'Aggregated Copilot Analytics Team' `
    -SignerEmail 'team@example.invalid'
```

This workflow:

  1. Builds the `.zip` package deterministically and captures its
     SHA-256.
  2. Loads the signing cert from CurrentUser\My (or from a PFX path
     when `-PfxPath` + a SecureString password are supplied).
  3. Produces a detached signature of the package by signing its
     SHA-256 hash with the cert's RSA private key. The signature is
     written to `<filename>.zip.sig` as the JSON envelope above.
  4. Emits a `<filename>.zip.signer.json` with the signing
     certificate's SHA-1 thumbprint, subject, algorithm, and the
     UTC timestamp of the signing operation.
  5. Immediately self-verifies the just-written signature using the
     appliance-side verifier (`Signature.psm1`). If self-verification
     fails, both sidecars are deleted and the script exits non-zero.
  6. Publishes all three files alongside the manifest. The manifest
     itself is **not** signed in this version; signature trust is
     anchored at the package level.
  7. Updates the release notes with the SHA-256 (human-verifiable),
     the signer's certificate thumbprint, and a link to the signing
     key's public certificate.
  8. The chef adds the release team's thumbprint to
     `<workspace>\Trust\trusted-signers.json` once, out of band.
     Subsequent downloads match the per-package signer thumbprint
     against this allowlist.

### What this workflow does *not* establish

The trust chain above intentionally **does not**:

  - Touch the Windows certificate store or any other OS trust store.
  - Bypass SmartScreen, AppLocker, or Mark-of-the-Web.
  - Use a cloud key vault, an automated CI signer, or any remote
    signing service.
  - Authenticate the manifest. Manifest authenticity is delegated to
    HTTPS at fetch time; the broker does not persist any per-fetch
    trust artifact for the manifest.
  - Auto-install the update. The Cookbook never auto-applies a
    staged package; the chef runs the installer explicitly.
  - Provide forward secrecy or revocation. If a signing key is
    compromised the chef's response is to remove the entry from
    `trusted-signers.json` by hand.

### Explicit non-goals for this slice

The following are **out of scope** for the slice that introduced
the read-only trust evaluator and are not implemented anywhere in
the code:

  - Production certificate issuance.
  - Cloud signing services or remote key vaults.
  - Automated CI signing pipelines.
  - OS trust-store mutation.
  - SmartScreen / Mark-of-the-Web bypass.
  - Installer elevation tricks.
  - Hidden trust overrides, fail-open paths, or silent downgrades on
    verification failure.

The slice's contribution is to make the **shape** of the future
trust chain deterministic so that a real verifier can be slotted in
without changing the shipping download/staging surface, and to make
the **current reality** legible to chefs so that no green badge
ever overstates what the appliance can actually attest.
