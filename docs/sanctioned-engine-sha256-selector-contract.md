# Sanctioned Engine — SHA-256 Certificate Selector Contract

Status: **external behavior contract for a FUTURE sanctioned PAX engine.**

This document defines what a PAX engine must do in order to truthfully attest the
capability token `organization_certificate_sha256_selector_v1` in an approved
engine manifest (schema v2).

It is documentation only. PAX Cookbook does not modify, patch, wrap, re-sign, or
regenerate engine bytes, and nothing in this cycle changed the engine that is
currently shipped or acquired.

## Why this contract exists

An organization-provided key is referenced by a SHA-256 hash over the
certificate's DER bytes. The engine that PAX Cookbook ships and acquires today
can only select a certificate by SHA-1 thumbprint, and its selection logic is not
fail-closed. PAX Cookbook therefore refuses every organization-bound Cook.

A future sanctioned engine can remove that refusal — but only if it implements
the behavior below exactly. PAX Cookbook never translates a SHA-256 reference
down to a SHA-1 thumbprint, and never accepts a looser selector.

## Required engine behavior

An engine that attests `organization_certificate_sha256_selector_v1` MUST:

1. Accept a parameter named `-ClientCertificateSha256`.
2. Require the value to be exactly 64 hexadecimal characters, and normalize it to
   uppercase before use.
3. Interpret the value as SHA-256 computed over the certificate's DER encoding.
4. Search **exactly** the `LocalMachine\My` certificate store.
5. **Never** search `CurrentUser\My`. The both-store fallback present in the
   current engine is explicitly rejected by this contract.
6. Compute SHA-256 over each candidate certificate's DER bytes and compare
   against the normalized reference.
7. Require **exactly one** match:
   * zero matches is a hard failure;
   * more than one match is a hard failure. The private-key duplicate
     *preference* present in the current engine is explicitly rejected by this
     contract — an ambiguous reference must never resolve to an arbitrary
     certificate.
8. Require an accessible private key for the single matched certificate.
9. **Never** fall back to SHA-1 thumbprint selection, for any reason.
10. Preserve existing app-registration behavior for the legacy
    `-ClientCertificateThumbprint` selector when that selector is used instead.
11. Fail **before** authentication on a malformed, unmatched, or ambiguous
    selection. Selection failure must never become an authentication attempt.

## How PAX Cookbook consumes the attestation

* The capability is declared per entry in an approved engine manifest with
  `schemaVersion` 2, in a closed `capabilities` array. Unknown tokens, duplicate
  tokens, non-string elements, a non-array value, and over-limit arrays are all
  hard manifest rejections.
* A schema v1 manifest entry cannot carry `capabilities` at all: the v1 entry
  allow-list is closed, so such an entry is rejected as an unknown field. Schema
  v1 therefore always means "no capability".
* Capability tokens are persisted into the acquisition record in the **same**
  atomic write as the acquired engine's version and SHA-256, and only after the
  acquired bytes have been verified against the approved hash. A failed
  acquisition persists no capability.
* At runtime PAX Cookbook re-verifies the acquired engine's bytes against the
  recorded approved hash and requires the capability record to be bound to the
  recorded acquisition version. Any mismatch, malformation, or unreadable state
  fails closed.
* Only a bounded state name ever crosses a boundary. The engine version, engine
  hash, manifest identity, capability token, and certificate reference are never
  shown to the customer, logged, or placed in a command preview.

## What this contract does not grant

Attesting this capability makes an organization-bound Recipe *preparable*. It
does not authorize a Bake, does not assert tenant acceptance or entitlement, does
not verify authentication, and does not test execution. Those remain separate,
later decisions.
