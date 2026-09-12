# Sanctioned Engine — Candidate Intake Specification

Status: **normative intake specification for an upstream PAX engine owner.**

Audience: the upstream producer who builds, signs, and delivers a candidate PAX
engine that claims the capability token
`organization_certificate_sha256_selector_v1`.

This document is documentation only. It authors no code, ships no bundle, signs
nothing, and changes no engine bytes. PAX Cookbook does not modify, patch, wrap,
re-sign, or regenerate engine bytes at any point described here.

Key words `MUST`, `MUST NOT`, `REQUIRED`, and `REJECT` are binding. A bundle that
fails any `MUST`, violates any `MUST NOT`, omits any `REQUIRED` item, or triggers
any `REJECT` condition does not enter PAX Cookbook.

Every JSON block in this document is **NON-DEPLOYABLE**. Values shown as
`<PLACEHOLDER-...>` are illustrative placeholders only and MUST NOT be copied
into a real bundle.

---

## 1. Purpose and scope

### 1.1 Purpose

PAX Cookbook today refuses every organization-bound Cook, because the engine it
ships and acquires can only select a certificate by SHA-1 thumbprint and its
selection logic is not fail-closed. A future sanctioned engine can remove that
refusal.

This specification defines exactly what an upstream producer MUST deliver so
that PAX Cookbook can perform an **offline intake evaluation** of a candidate
engine that claims `organization_certificate_sha256_selector_v1`.

### 1.2 In scope

* The complete candidate bundle layout.
* The candidate engine artifact requirements.
* The approved engine manifest, schema v2.
* The detached signature / provenance envelope.
* The byte-bound conformance evidence index and its raw evidence files.
* The required runtime conformance matrix.
* The intake verification sequence and its cryptographic binding chain.
* The three intake verdicts, the rejection conditions, and the non-grants.

### 1.3 Out of scope

* Any live acquisition, download, install, activation, Bake, or Cook.
* Any change to the shipped engine or to any PAX Cookbook protected state.
* Any tenant onboarding, entitlement, licensing, or authorization decision.
* Any signing operation performed by PAX Cookbook. PAX Cookbook holds no
  private key for this workflow and MUST NOT be asked to sign anything.

### 1.4 What intake is

Intake is an **offline, read-only evaluation of supplied bytes**. During intake
PAX Cookbook:

* MUST NOT open, read, enumerate, or mutate any certificate store.
* MUST NOT execute the candidate engine.
* MUST NOT perform network fetches, including of `downloadUrl`.
* MUST NOT alter installation state, registry, ProgramData, or ACLs.
* MUST NOT run PAX or a Bake.

---

## 2. Relationship to the behavior contract

The authoritative statement of *what the engine must do at runtime* is:

`docs/sanctioned-engine-sha256-selector-contract.md`

That document is **unchanged and authoritative**. It contains eleven numbered
requirements (1 through 11) under "Required engine behavior".

This specification:

* MUST NOT restate those requirements as a competing normative source.
* MUST cite them **by their existing numbers**.
* MUST NOT renumber, reword, extend, or edit them.

Division of responsibility:

| Document | Answers |
| --- | --- |
| Behavior contract | What the sanctioned engine MUST do at runtime |
| This specification | What the upstream producer MUST deliver so that claim can be evaluated |

If the two ever appear to conflict on runtime behavior, the behavior contract
wins and the bundle is REJECTED until the conflict is resolved upstream.

---

## 3. Complete bundle layout

### 3.1 Required members

The candidate bundle is a single directory tree. It MUST contain exactly the
following members, and nothing else that is prohibited by section 3.3.

| # | Bundle-root-relative path | Required | Description |
| --- | --- | --- | --- |
| 1 | `<candidate-engine-script-file>` | REQUIRED | The candidate engine script, exactly as it would be published |
| 2 | `<candidate-changelog-file>` | REQUIRED | Human-readable changelog for the candidate version |
| 3 | `approved-engine-manifest.json` | REQUIRED | Approved engine manifest, schema v2 |
| 4 | `approved-engine-manifest.json.sig` | REQUIRED | Detached signature envelope over member 3 |
| 5 | `selector-conformance-evidence.json` | REQUIRED | Byte-bound conformance evidence index |
| 6 | `selector-conformance-evidence.json.sig` | REQUIRED | Detached signature envelope over member 5 |
| 7 | `<conformance-harness-archive>` | REQUIRED | Archive of the harness that produced the evidence |
| 8 | `<raw-evidence-directory>/<one file per required case>` | REQUIRED | Exactly one raw evidence file per required conformance case |

The signature filenames in rows 4 and 6 MUST be the exact literal names shown.
The `.sig` suffix convention matches how PAX Cookbook stages a manifest
signature during acquisition (`approved-engine-manifest.json.sig`).

### 3.2 Path rules

* Every path recorded anywhere in the bundle MUST be **bundle-root-relative**.
* Paths MUST use forward slashes.
* Paths MUST NOT contain `..` traversal segments.
* Paths MUST NOT be absolute, drive-qualified, or UNC.
* Paths MUST NOT reference an NTFS alternate data stream.
* Paths MUST NOT rely on a symlink, junction, hard link, or any other reparse
  indirection.
* No two recorded paths may differ only by case.

### 3.3 Prohibited bundle contents

The bundle MUST NOT contain:

* Any additional executable, installer, binary, DLL, MSI, or script beyond the
  single candidate engine script and the harness archive.
* Any `.pfx`, `.p12`, `.key`, `.pem` private key, or any other private key
  material, encrypted or not.
* Any tenant identifier, client identifier, application identifier, directory
  identifier, user principal name, account name, mailbox, or customer record.
* Any organization certificate identifier, subject, serial number, or
  certificate thumbprint other than the signer identity fields required by
  section 6.
* Any secret, password, token, bearer credential, connection string, or
  API key.
* A duplicate of the current protected engine presented as a candidate.

### 3.4 The candidate MUST be new bytes

The current protected engine SHA-256 is:

`99AB97232C76022771197B84AF880ED48D9E4B83F678F05262C38A223D3814C9`

The candidate engine SHA-256 **MUST differ** from that value. A bundle whose
candidate SHA-256 equals the current protected engine SHA-256 is REJECTED as a
re-presentation of already-shipped bytes, regardless of how well the rest of the
bundle is formed.

---

## 4. Candidate engine requirements

The candidate engine artifact MUST:

1. Implement every numbered requirement 1 through 11 of the behavior contract.
2. Be delivered as **final published bytes**. The bytes in the bundle, the bytes
   hashed in the manifest entry, the bytes hashed in the conformance evidence,
   and the bytes served by `downloadUrl` MUST all be identical.
3. Carry a version string that parses as a .NET-style dotted version, because
   PAX Cookbook parses entry versions when selecting an entry.
4. Be a single file. Multi-file engines are REJECTED.
5. Not require PAX Cookbook to modify it in any way before use.

The candidate engine MUST NOT:

* Depend on PAX Cookbook rewriting, wrapping, patching, or re-signing it.
* Require any PAX Cookbook-side certificate store access.
* Emit an engine version, engine hash, manifest identity, capability token, or
  certificate reference into any customer-visible surface, log, or command
  preview. Only a bounded state name ever crosses a boundary.

---

## 5. Approved manifest schema v2

### 5.1 Closed top-level schema

`approved-engine-manifest.json` MUST have a **closed** top-level object with
exactly these six fields. Any unknown top-level field is a hard REJECT.

| Field | Type | Requirement |
| --- | --- | --- |
| `schemaVersion` | integer | MUST be exactly `2` |
| `manifestVersion` | integer or non-empty string | REQUIRED |
| `channel` | string | REQUIRED |
| `generatedAtUtc` | string | REQUIRED, UTC timestamp |
| `signingKeyId` | non-empty string | REQUIRED |
| `scripts` | array | REQUIRED |

### 5.2 Closed entry schema (schema v2)

Each element of `scripts` MUST be a JSON object with a **closed** field set.
Any unknown entry field is a hard REJECT.

| Field | Type | Requirement |
| --- | --- | --- |
| `name` | non-empty string | REQUIRED |
| `version` | non-empty string | REQUIRED |
| `sha256` | string | REQUIRED, 64 hex characters, uppercase |
| `downloadUrl` | string | REQUIRED, `https://` |
| `status` | string | REQUIRED, MUST be `"approved"` for the candidate entry |
| `minCookbookVersion` | non-empty string | REQUIRED |
| `maxCookbookVersion` | non-empty string | REQUIRED |
| `releaseNotesUrl` | string | OPTIONAL; when present MUST be `https://` |
| `capabilities` | array of strings | REQUIRED in schema v2 |

### 5.3 The capability array

For the candidate entry, `capabilities` MUST be exactly:

`["organization_certificate_sha256_selector_v1"]`

Rules:

* The array MUST contain that token exactly once.
* The array MUST NOT contain any unrecognized token. Unrecognized tokens are a
  hard REJECT.
* The array MUST NOT contain a duplicate token.
* Every element MUST be a non-empty string.
* The value MUST be a JSON array, not a string or object.
* The array MUST contain **no more than eight** entries. The manifest capability
  limit is eight capability entries per `scripts` entry, so a ninth entry is a
  hard REJECT. This numeric limit is a ceiling, not a licence to pad: the
  candidate entry MUST still contain exactly the one sanctioned capability token
  shown above.

A schema v1 entry cannot carry `capabilities` at all — the v1 entry allow-list
is closed, so such an entry is rejected as an unknown field. A candidate bundle
therefore MUST NOT claim the capability in a schema v1 manifest. Schema v1
always means "no capability".

### 5.4 SHA-256 formatting

`sha256` MUST be 64 hexadecimal characters. It MUST be supplied uppercase.
PAX Cookbook normalizes to uppercase for comparison; supplying uppercase removes
any ambiguity in evidence review.

### 5.5 Entry uniqueness

The candidate engine entry MUST appear **exactly once** in `scripts`. Two
entries describing the same candidate — even with identical content — are a
REJECT, because a duplicated candidate makes selection provenance ambiguous.

### 5.6 FINDING A — the version range decides selectability

PAX Cookbook's entry selection skips any entry where the shipping PAX Cookbook
version is below `minCookbookVersion` or above `maxCookbookVersion`, and it also
skips any entry whose `minCookbookVersion`, `maxCookbookVersion`, or `version`
does not parse as a version.

Therefore:

* The candidate entry's `minCookbookVersion`..`maxCookbookVersion` range **MUST
  admit the shipping PAX Cookbook version** that will evaluate the bundle.
* All three version strings MUST be parseable.

A perfectly signed, perfectly capable entry whose range excludes the shipping
version can **never** be selected. Producers MUST confirm the shipping version
with PAX Cookbook before generating the manifest.

### 5.7 Illustrative manifest — NON-DEPLOYABLE

```json
{
  "schemaVersion": 2,
  "manifestVersion": "<PLACEHOLDER-MANIFEST-VERSION>",
  "channel": "<PLACEHOLDER-CHANNEL>",
  "generatedAtUtc": "<PLACEHOLDER-UTC-TIMESTAMP>",
  "signingKeyId": "<PLACEHOLDER-SIGNING-KEY-ID>",
  "scripts": [
    {
      "name": "<PLACEHOLDER-ENGINE-NAME>",
      "version": "<PLACEHOLDER-CANDIDATE-VERSION>",
      "sha256": "<PLACEHOLDER-64-UPPERCASE-HEX-CANDIDATE-SHA256>",
      "downloadUrl": "https://<PLACEHOLDER-HOST>/<PLACEHOLDER-PATH>",
      "status": "approved",
      "minCookbookVersion": "<PLACEHOLDER-MIN-VERSION>",
      "maxCookbookVersion": "<PLACEHOLDER-MAX-VERSION>",
      "releaseNotesUrl": "https://<PLACEHOLDER-HOST>/<PLACEHOLDER-NOTES>",
      "capabilities": ["organization_certificate_sha256_selector_v1"]
    }
  ]
}
```

**NON-DEPLOYABLE.** Placeholder values only.

### 5.8 `downloadUrl`

`downloadUrl` MUST be an `https://` URL and MUST serve **the exact candidate
bytes** whose SHA-256 appears in the entry.

It is **not fetched during offline intake**. It will be fetched during any later
authorized acquisition test, at which point the served bytes are verified
against the approved hash. A `downloadUrl` that serves different bytes will fail
that later test even though intake accepted the bundle.

---

## 6. Detached signature / provenance envelope

### 6.1 Closed nine-field schema

A signature envelope is a JSON object with a **closed** nine-field schema. Every
field is REQUIRED and any unknown field is a hard REJECT.

| # | Field | Requirement |
| --- | --- | --- |
| 1 | `schemaVersion` | integer, MUST be exactly `1` |
| 2 | `packageFile` | non-empty string, the signed file's name |
| 3 | `packageSha256` | 64-character SHA-256 hex digest of the signed bytes |
| 4 | `hashAlgorithm` | MUST be exactly `"SHA256"` |
| 5 | `signatureAlgorithm` | MUST be exactly `"RSA-PKCS1v15-SHA256"` |
| 6 | `signatureBase64` | base64 raw signature over the SHA-256 hash |
| 7 | `signerCertBase64` | base64 DER of the signer certificate (public only) |
| 8 | `signerCertThumbprint` | 40-character SHA-1 hex thumbprint of the signer cert |
| 9 | `signedAtUtc` | UTC timestamp string |

This exact schema applies to **both** `approved-engine-manifest.json.sig` and
`selector-conformance-evidence.json.sig`.

### 6.2 Illustrative envelope — NON-DEPLOYABLE

```json
{
  "schemaVersion": 1,
  "packageFile": "approved-engine-manifest.json",
  "packageSha256": "<PLACEHOLDER-64-HEX-PACKAGE-SHA256>",
  "hashAlgorithm": "SHA256",
  "signatureAlgorithm": "RSA-PKCS1v15-SHA256",
  "signatureBase64": "<PLACEHOLDER-BASE64-SIGNATURE>",
  "signerCertBase64": "<PLACEHOLDER-BASE64-DER-PUBLIC-CERT>",
  "signerCertThumbprint": "<PLACEHOLDER-40-HEX-SHA1-THUMBPRINT>",
  "signedAtUtc": "<PLACEHOLDER-UTC-TIMESTAMP>"
}
```

**NON-DEPLOYABLE.** Placeholder values only.

### 6.3 What signature verification does — and what it does not do

PAX Cookbook's signature verifier performs exactly four things:

1. Closed-schema validation of the nine-field envelope.
2. Recomputation of SHA-256 over the package bytes and comparison against
   `packageSha256`.
3. A self-check that the embedded `signerCertBase64` certificate's own SHA-1
   thumbprint equals `signerCertThumbprint`.
4. RSA verification of `signatureBase64` against the SHA-256 hash using PKCS#1
   v1.5 padding and the embedded certificate's public key.

### 6.4 FINDING D — trust-anchor pinning is the caller's job

The signature verifier **does not pin trust**. It proves only that *some*
certificate consistently signed those exact bytes.

Trust pinning is performed by the **caller**: the acquisition route compares the
verified certificate thumbprint against the pinned
`EngineManifestTrustAnchorThumbprint` value carried in `VERSION.json`, and fails
with a trust-anchor mismatch when they differ.

This specification MUST NOT be read as saying the verifier enforces trust by
itself. A bundle signed by an unpinned certificate will pass envelope
verification and still be rejected at the pinning comparison. The producer's
signing certificate MUST therefore be the certificate PAX Cookbook has pinned,
established out of band before the bundle is submitted.

### 6.5 The SHA-1 thumbprint is signer identity only

`signerCertThumbprint` is 40 hex characters and is **SHA-1 by existing design**
of the signature envelope. It identifies the *signer* of a package.

It **MUST NEVER** be used, mapped, translated, or repurposed as an
organization-certificate SHA-1 selector bridge. The entire point of the
behavior contract is that organization certificates are selected by SHA-256 over
DER and never by SHA-1. Reusing this field as a selector value is a hard REJECT
and would silently reintroduce the weakness the sanctioned engine exists to
remove.

### 6.6 What is verified in production today

At acquisition today PAX Cookbook verifies **only the manifest envelope**
(`approved-engine-manifest.json.sig`).

The conformance-evidence envelope
(`selector-conformance-evidence.json.sig`) is an **intake-time requirement**,
to be checked by a future intake validator. It is **not** a current production
runtime behavior. Nothing in this document should be read as claiming production
support that does not exist.

---

## 7. Byte-bound conformance evidence

### 7.1 Closed evidence index schema

`selector-conformance-evidence.json` MUST be a closed-schema JSON object.
Any unknown field is a hard REJECT.

| Field | Type | Requirement |
| --- | --- | --- |
| `schemaVersion` | integer | MUST be exactly `1` |
| `capability` | string | MUST be exactly `organization_certificate_sha256_selector_v1` |
| `generatedAtUtc` | string | REQUIRED, UTC timestamp |
| `candidate` | object | REQUIRED, see 7.2 |
| `manifest` | object | REQUIRED, see 7.2 |
| `harness` | object | REQUIRED, see 7.2 |
| `cases` | array of objects | REQUIRED, see 7.3 |
| `summary` | object | REQUIRED, see 7.4 |

### 7.2 Bound object shapes

| Object | Required fields |
| --- | --- |
| `candidate` | `file`, `version`, `sha256`, `byteCount` |
| `manifest` | `file`, `sha256`, `manifestVersion`, `signingKeyId` |
| `harness` | `file`, `sha256`, `version` |

* Every `sha256` MUST be 64 uppercase hex characters.
* `candidate.sha256` MUST equal the manifest entry `sha256`.
* `candidate.byteCount` MUST equal the actual byte length of the candidate file.
* `manifest.sha256` MUST equal the SHA-256 of `approved-engine-manifest.json`.
* `manifest.manifestVersion` and `manifest.signingKeyId` MUST equal the manifest's
  own values.
* `harness.sha256` MUST equal the SHA-256 of the harness archive in the bundle.

### 7.3 Case entries

Each element of `cases` MUST have exactly:

| Field | Requirement |
| --- | --- |
| `id` | Stable case ID from section 8, unique within the array |
| `result` | MUST be `PASS` |
| `exitCode` | MUST be `0` |
| `rawEvidencePath` | Bundle-root-relative path to that case's raw evidence file |
| `rawEvidenceSha256` | 64 uppercase hex SHA-256 of that raw evidence file |

Rules:

* Every REQUIRED case ID in section 8 MUST appear exactly once.
* A missing required case ID is a REJECT.
* A duplicate case ID is a REJECT.
* A raw evidence file present in the bundle but not indexed is a REJECT.
* An indexed `rawEvidencePath` that does not exist is a REJECT.
* Any recomputed `rawEvidenceSha256` mismatch is a REJECT.
* Any machine-absolute path, drive letter, UNC path, home directory, or build
  agent name anywhere in the evidence is a REJECT.
* Any tenant, client, account, certificate, key, token, or secret value anywhere
  in the evidence is a REJECT.

### 7.4 Summary

`summary` MUST have exactly `requiredCaseCount`, `passedCaseCount`, and
`failedCaseCount`.

* `requiredCaseCount` MUST equal the number of REQUIRED cases in section 8.
* `passedCaseCount` MUST equal `requiredCaseCount`.
* `failedCaseCount` MUST be `0`.
* `cases.length` MUST equal `requiredCaseCount`.

A bundle with any failing case is not a candidate. Partial conformance is not
accepted, negotiated, or waived.

### 7.5 Raw evidence file shape

Each raw evidence file MUST begin with one **canonical header**. The header is
byte-deterministic: two conforming harnesses producing the same case against the
same bytes produce the same header bytes.

Each raw evidence file MUST:

* be UTF-8 with **no BOM**;
* begin at **byte zero** with exactly one **compact** JSON object;
* terminate that first JSON object with exactly **one LF** byte, with **no CR**
  before the LF;
* contain exactly these seven fields, in exactly this order:
  1. `schemaVersion`
  2. `caseId`
  3. `candidateSha256`
  4. `harnessSha256`
  5. `result`
  6. `exitCode`
  7. `authenticationCallbackCount`

| Header field | Requirement |
| --- | --- |
| `schemaVersion` | integer, MUST be exactly `1` |
| `caseId` | the stable case ID from section 8 for this file |
| `candidateSha256` | 64 uppercase hex; the candidate engine SHA-256 as recomputed by the harness at run time |
| `harnessSha256` | 64 uppercase hex; the harness archive SHA-256 |
| `result` | MUST be exactly `"PASS"` |
| `exitCode` | integer, MUST be exactly `0` |
| `authenticationCallbackCount` | non-negative integer; see the semantics rules below |

The canonical header shape is exactly:

`{"schemaVersion":1,"caseId":"<CASE-ID>","candidateSha256":"<64-HEX-CANDIDATE-SHA256>","harnessSha256":"<64-HEX-HARNESS-SHA256>","result":"PASS","exitCode":0,"authenticationCallbackCount":0}`

**NON-DEPLOYABLE.** The angle-bracketed values above are illustrative
placeholders only and MUST NOT be copied into a real bundle.

Further header rules:

* **No whitespace** is permitted anywhere inside the header JSON.
* Free-form bounded output MAY follow, but ONLY after that first LF byte.
* The `rawEvidenceSha256` recorded in the evidence index covers the header, the
  LF, and every following byte of the file.
* Any malformed, reordered, missing, duplicated, or unknown header field is a
  hard REJECT.

`authenticationCallbackCount` semantics:

* **Definition.** `authenticationCallbackCount` is the TOTAL number of
  authentication-callback invocations during the ENTIRE case run, MEASURED AT
  CASE EXIT. It is not a snapshot taken at selection time, and it is not a bound
  on a single moment.
* **Value.** It MUST be `0` for ALL THIRTEEN cases. The conformance harness
  remains tenant-free, network-free, and authentication-free.
* A SUCCESSFUL case proves certificate-**selection** behavior only; it stops
  before authentication and never attempts one.
* `CASE-12` preserves legacy thumbprint **selection** compatibility. It is NOT a
  live authentication attempt, and its expected count is `0` exactly like every
  other case.

The recomputed candidate SHA-256 in every raw evidence file MUST equal
`candidate.sha256` and the manifest entry `sha256`. This is what makes the
evidence **byte-bound**: the attestation is bound to those exact engine bytes,
not to "a build of that version".

Raw evidence MUST contain only bounded content: case IDs, hashes, byte counts,
result tokens, exit codes, counts, and bounded state names. It MUST NOT contain
certificate subjects, serial numbers, thumbprints, tenant or account values,
tokens, private keys, or machine-absolute paths.

### 7.6 Illustrative evidence index — NON-DEPLOYABLE

```json
{
  "schemaVersion": 1,
  "capability": "organization_certificate_sha256_selector_v1",
  "generatedAtUtc": "<PLACEHOLDER-UTC-TIMESTAMP>",
  "candidate": {
    "file": "<PLACEHOLDER-CANDIDATE-FILE>",
    "version": "<PLACEHOLDER-CANDIDATE-VERSION>",
    "sha256": "<PLACEHOLDER-64-HEX-CANDIDATE-SHA256>",
    "byteCount": 0
  },
  "manifest": {
    "file": "approved-engine-manifest.json",
    "sha256": "<PLACEHOLDER-64-HEX-MANIFEST-SHA256>",
    "manifestVersion": "<PLACEHOLDER-MANIFEST-VERSION>",
    "signingKeyId": "<PLACEHOLDER-SIGNING-KEY-ID>"
  },
  "harness": {
    "file": "<PLACEHOLDER-HARNESS-ARCHIVE>",
    "sha256": "<PLACEHOLDER-64-HEX-HARNESS-SHA256>",
    "version": "<PLACEHOLDER-HARNESS-VERSION>"
  },
  "cases": [
    {
      "id": "CASE-01-SHA256-PARAMETER-ACCEPTED",
      "result": "PASS",
      "exitCode": 0,
      "rawEvidencePath": "<PLACEHOLDER-EVIDENCE-DIR>/<PLACEHOLDER-CASE-01-FILE>",
      "rawEvidenceSha256": "<PLACEHOLDER-64-HEX-RAW-EVIDENCE-SHA256>"
    }
  ],
  "summary": {
    "requiredCaseCount": 13,
    "passedCaseCount": 13,
    "failedCaseCount": 0
  }
}
```

**NON-DEPLOYABLE.** Placeholder values only; the `cases` array is truncated for
illustration and MUST carry all thirteen required cases in a real bundle.

### 7.7 FINDING C — certificate fixtures are the producer's responsibility

Several REQUIRED cases — a CurrentUser-only certificate, multiple LocalMachine
matches, and an inaccessible private key — are impossible to demonstrate without
certificate fixtures.

Therefore:

* The upstream producer **MAY** use ephemeral certificates, isolated or
  throwaway certificate stores, containerized or disposable build agents, or an
  injectable certificate-store abstraction **on their own build agents** to
  create these fixtures.
* Fixture certificates MUST be purpose-generated for conformance testing. They
  MUST NOT be real organization certificates, and their private keys MUST NOT
  leave the producer's build environment or appear in the bundle.
* The rule that no real certificate store is opened binds **PAX Cookbook**, not
  the producer. **PAX Cookbook opens, reads, enumerates, and mutates NO
  certificate store at intake.**

This distinction is deliberate: a rule the producer cannot satisfy would make
the required matrix undemonstrable.

---

## 8. Required conformance matrix

All thirteen cases are REQUIRED. Case IDs are stable and MUST be used verbatim.

For every case the evidence MUST record the authentication-callback invocation
count in the canonical header of section 7.5, and that count MUST be `0` for
**every** case — not only for the selection-failure cases. A successful case
proves certificate-selection behavior only and stops before authentication; a
selection failure MUST NOT become an authentication attempt. The `Auth
callbacks` column below is therefore the literal expected value of
`authenticationCallbackCount` measured at case exit.

| Case ID | Contract reqs | Fixture shape | Expected bounded outcome | Auth callbacks | Prohibited behavior proven absent |
| --- | --- | --- | --- | --- | --- |
| `CASE-01-SHA256-PARAMETER-ACCEPTED` | 1 | Valid 64-hex reference; one matching LocalMachine\My cert with accessible key | Parameter `-ClientCertificateSha256` accepted and bound; selection succeeds | 0 | Parameter absent or silently ignored |
| `CASE-02-MALFORMED-REFERENCE-REJECTED` | 2, 11 | 63-char, 65-char, and non-hex references | Hard failure, bounded error, before authentication | 0 | Truncation, padding, coercion, or auth attempt |
| `CASE-03-LOWERCASE-REFERENCE-NORMALIZED` | 2 | Lowercase 64-hex reference for a present cert | Normalized to uppercase and matched | 0 | Case-sensitive miss; any non-normalizing compare |
| `CASE-04-DER-SHA256-SEMANTICS` | 3, 6 | Cert whose DER SHA-256 is known; PEM/text digest deliberately different | Match occurs on DER-bytes SHA-256 only | 0 | Hashing PEM text, base64, or any non-DER encoding |
| `CASE-05-LOCALMACHINE-SINGLE-MATCH` | 4, 6, 7, 8 | Exactly one matching cert in LocalMachine\My with accessible private key | Selection succeeds; exactly one match | 0 | Searching any store other than LocalMachine\My |
| `CASE-06-CURRENTUSER-ONLY-NOT-SEARCHED` | 5, 7, 11 | Matching cert present ONLY in CurrentUser\My | Hard failure: zero matches; CurrentUser\My never searched | 0 | Both-store fallback; CurrentUser\My match |
| `CASE-07-ZERO-MATCH-FAILS-CLOSED` | 7, 11 | Valid reference matching no installed cert | Hard failure, bounded error, fail-closed | 0 | Default cert, nearest match, or silent continue |
| `CASE-08-MULTIPLE-MATCH-AMBIGUOUS` | 7, 11 | Two distinct LocalMachine\My certs matching the reference | Hard failure on ambiguity | 0 | Arbitrary or first-match resolution |
| `CASE-09-DUPLICATE-PRIVATE-KEY-PREFERENCE-REJECTED` | 7, 11 | Two matches, exactly one with an accessible private key | Hard failure on ambiguity — the private-key preference is NOT applied | 0 | Preferring the key-bearing duplicate |
| `CASE-10-PRIVATE-KEY-INACCESSIBLE` | 8, 11 | Exactly one match whose private key is absent or inaccessible | Hard failure, bounded error, before authentication | 0 | Proceeding without a usable private key |
| `CASE-11-NO-SHA1-FALLBACK` | 9, 11 | SHA-256 selection fails while a cert matching the equivalent SHA-1 thumbprint exists | Hard failure; no SHA-1 selection attempted | 0 | Any SHA-1 thumbprint fallback path |
| `CASE-12-LEGACY-THUMBPRINT-SELECTOR-PRESERVED` | 10 | `-ClientCertificateThumbprint` used instead of the SHA-256 selector | Existing app-registration selection behavior preserved and unchanged | 0 | Regression or removal of the legacy selector |
| `CASE-13-PRE-AUTHENTICATION-ORDERING` | 11 | Malformed, unmatched, and ambiguous references in sequence | Every failure occurs strictly before any authentication step | 0 | Any authentication attempt after selection failure |

### 8.1 Requirement coverage — contract requirement to cases

Every one of the eleven numbered behavior-contract requirements is covered by at
least one REQUIRED case:

| Contract requirement | Covering cases | Count |
| --- | --- | --- |
| 1 | `CASE-01` | 1 |
| 2 | `CASE-02`, `CASE-03` | 2 |
| 3 | `CASE-04` | 1 |
| 4 | `CASE-05` | 1 |
| 5 | `CASE-06` | 1 |
| 6 | `CASE-04`, `CASE-05` | 2 |
| 7 | `CASE-05`, `CASE-06`, `CASE-07`, `CASE-08`, `CASE-09` | 5 |
| 8 | `CASE-05`, `CASE-10` | 2 |
| 9 | `CASE-11` | 1 |
| 10 | `CASE-12` | 1 |
| 11 | `CASE-02`, `CASE-06`, `CASE-07`, `CASE-08`, `CASE-09`, `CASE-10`, `CASE-11`, `CASE-13` | 8 |

Uncovered requirements: **0 of 11**.

### 8.2 Requirement coverage — case to contract requirements

Every REQUIRED case maps back to at least one numbered requirement:

| Case ID | Requirements | Count |
| --- | --- | --- |
| `CASE-01-SHA256-PARAMETER-ACCEPTED` | 1 | 1 |
| `CASE-02-MALFORMED-REFERENCE-REJECTED` | 2, 11 | 2 |
| `CASE-03-LOWERCASE-REFERENCE-NORMALIZED` | 2 | 1 |
| `CASE-04-DER-SHA256-SEMANTICS` | 3, 6 | 2 |
| `CASE-05-LOCALMACHINE-SINGLE-MATCH` | 4, 6, 7, 8 | 4 |
| `CASE-06-CURRENTUSER-ONLY-NOT-SEARCHED` | 5, 7, 11 | 3 |
| `CASE-07-ZERO-MATCH-FAILS-CLOSED` | 7, 11 | 2 |
| `CASE-08-MULTIPLE-MATCH-AMBIGUOUS` | 7, 11 | 2 |
| `CASE-09-DUPLICATE-PRIVATE-KEY-PREFERENCE-REJECTED` | 7, 11 | 2 |
| `CASE-10-PRIVATE-KEY-INACCESSIBLE` | 8, 11 | 2 |
| `CASE-11-NO-SHA1-FALLBACK` | 9, 11 | 2 |
| `CASE-12-LEGACY-THUMBPRINT-SELECTOR-PRESERVED` | 10 | 1 |
| `CASE-13-PRE-AUTHENTICATION-ORDERING` | 11 | 1 |

Unmapped cases: **0 of 13**.

The required case set is **CLOSED**. A bundle MUST contain exactly the thirteen
case IDs listed above — no more and no fewer.

* Any additional case entry in `cases`, beyond those thirteen, is a hard REJECT.
* The raw-evidence directory MUST contain exactly one indexed raw evidence file
  for each of those thirteen cases, and no additional case evidence.
* Supplementary cases are not accepted, negotiated, or waived. A producer with a
  further case to contribute raises it upstream so the required set can be
  revised deliberately; it is never smuggled into a bundle.

---

## 9. Intake verification sequence

Intake is performed offline against the supplied bytes, in this order. Any step
that fails stops the sequence and produces a rejection verdict.

1. **Structural intake.** Confirm all eight REQUIRED bundle members are present,
   confirm no prohibited content, and confirm every path satisfies section 3.2.
2. **Manifest schema.** Validate `approved-engine-manifest.json` against the
   closed schema v2 rules of section 5, including `status`, HTTPS URLs, the
   capability array, and single-entry uniqueness.
3. **Version-range admissibility.** Confirm the candidate entry's version range
   admits the shipping PAX Cookbook version and that all three version strings
   parse (Finding A).
4. **Manifest envelope.** Validate the nine-field envelope, recompute the
   manifest SHA-256, verify the embedded-certificate thumbprint self-check, and
   verify the RSA/PKCS#1 v1.5 SHA-256 signature.
5. **Trust anchor.** Compare the verified signer thumbprint to the pinned trust
   anchor value (Finding D). A mismatch is a rejection.
6. **Evidence envelope.** Apply steps 4 and 5 to
   `selector-conformance-evidence.json.sig`, using the same pinned signer. This
   is an intake-time check, not a production runtime behavior (section 6.6).
7. **Evidence index.** Validate the closed evidence schema, all bound object
   shapes, all thirteen REQUIRED case IDs, all `PASS` results, all exit codes
   `0`, and `failedCaseCount == 0`.
8. **Byte binding.** Recompute the candidate SHA-256, the manifest SHA-256, the
   harness SHA-256, and every `rawEvidenceSha256` from the supplied bytes and
   confirm each matches the index. Confirm the recomputed candidate SHA-256 in
   every raw evidence header equals the manifest entry `sha256`.
9. **Novelty.** Confirm the candidate SHA-256 differs from the current protected
   engine SHA-256 (section 3.4).
10. **Sensitive-content scan.** Confirm no prohibited identifier, secret, or
    machine-absolute path appears anywhere in the manifest, envelopes, evidence
    index, or raw evidence.
11. **Supplemental static inspection.** Read the candidate engine source for
    obvious contradictions of the behavior contract.
12. **Verdict.** Emit exactly one verdict from section 10.

### 9.1 The eight-link binding chain

Acceptance requires an unbroken chain. Any broken link REJECTS the bundle.

| Link | Binds | To |
| --- | --- | --- |
| 1 | Pinned trust anchor thumbprint | The signer certificate embedded in both envelopes |
| 2 | The signer certificate's RSA key | The manifest signature over the manifest hash |
| 3 | The manifest envelope `packageSha256` | The exact `approved-engine-manifest.json` bytes |
| 4 | The manifest entry `sha256` | The exact candidate engine bytes |
| 5 | The candidate engine bytes | `candidate.sha256` and `candidate.byteCount` in the evidence index |
| 6 | The evidence envelope signature | The exact `selector-conformance-evidence.json` bytes, under the same pinned signer |
| 7 | Each `rawEvidenceSha256` | The exact raw evidence file bytes for that case |
| 8 | The recomputed candidate SHA-256 inside every raw evidence file | The same candidate SHA-256 carried by the manifest entry |

Links 4, 5, and 8 together are what make the capability claim **byte-bound**:
the attestation applies to those specific bytes and to nothing else.

### 9.2 Static inspection is supplemental

Reading the candidate engine source is **supplemental only**. Static inspection
**cannot** prove:

* that a code path is unreachable at runtime;
* how ambiguity is actually resolved under load or under alternate inputs;
* whether a private key is genuinely accessible;
* that no fallback exists anywhere in the execution graph;
* that failure ordering truly precedes authentication.

Only the executed, byte-bound conformance evidence carries those claims. A
static read that appears to contradict the evidence is grounds for rejection; a
static read that appears to confirm the evidence adds no independent assurance.

### 9.3 What the chain does and does not prove

The chain proves **binding and accountability**: a specific pinned signer
attested a specific evidence set about specific engine bytes, and none of those
bytes changed afterward.

It does **not** prove upstream honesty. An approved signer could still attest
false evidence — for example, by running a harness that does not exercise what
it claims. The chain makes such an attestation attributable, non-repudiable, and
tied to exact bytes; it does not make it true. Intake acceptance is therefore a
statement about integrity and accountability, not a guarantee of correctness.

---

## 10. Acceptance verdicts

Intake emits exactly one of these three verdict tokens:

* `CANDIDATE_BUNDLE_ACCEPTED_FOR_NON_LIVE_ACQUISITION_TEST`
* `CANDIDATE_BUNDLE_REJECTED`
* `CANDIDATE_BUNDLE_INCOMPLETE`

| Verdict | Meaning |
| --- | --- |
| `CANDIDATE_BUNDLE_ACCEPTED_FOR_NON_LIVE_ACQUISITION_TEST` | Every REQUIRED item is present, the binding chain is unbroken, all thirteen cases pass, and the bundle is eligible to be considered for a later, separately authorized non-live acquisition test |
| `CANDIDATE_BUNDLE_REJECTED` | A present item is wrong: a schema violation, a signature or trust failure, a broken binding link, a failing case, prohibited content, or a contradicted claim |
| `CANDIDATE_BUNDLE_INCOMPLETE` | A REQUIRED member, case, or field is absent, so the bundle cannot be evaluated; resubmission with the missing items is expected |

### 10.1 These are intake outcomes, not Dev-Trio statuses

These three tokens are **intake outcomes about a bundle**. They are **NOT**
PAX Cookbook Dev-Trio machine statuses. The Dev-Trio machine statuses remain
exactly `TASK_COMPLETE`, `ERROR`, `DECISION_NEEDED`, and `HOLD`, and MUST NOT be
conflated with these verdicts in either direction.

### 10.2 FINDING B — the downstream blocker

**This is the single most important operational fact in this document.**

After a compatible entry is selected, PAX Cookbook cross-checks the selected
entry's `sha256` against the `paxScript.sha256` pin carried in `VERSION.json`
and **fails on mismatch** with a version-hash-mismatch error.

That pin currently holds the **CURRENT** engine SHA-256
(`99AB97232C76022771197B84AF880ED48D9E4B83F678F05262C38A223D3814C9`). Section
3.4 REQUIRES the candidate SHA-256 to differ from that value.

The consequence is unavoidable and MUST be understood by the upstream producer:

> **An accepted candidate still cannot be acquired.** Even a bundle that earns
> `CANDIDATE_BUNDLE_ACCEPTED_FOR_NON_LIVE_ACQUISITION_TEST` will fail the
> acquisition cross-check until PAX Cookbook updates the `VERSION.json`
> `paxScript.sha256` pin to the candidate SHA-256.

That pin update is a **PAX-Cookbook-side prerequisite** that:

* is performed in a **separately authorized cycle**;
* is **not performed** by intake;
* is **not authorized** by intake acceptance;
* is **not** an upstream deliverable and cannot be supplied by the producer.

A producer who ships a perfect bundle and expects acquisition to work
immediately is mistaken. Acceptance is a gate, not a switch.

---

## 11. Rejection conditions

Any one of the following REJECTS the bundle.

### 11.1 Structural

1. A REQUIRED bundle member is missing (`CANDIDATE_BUNDLE_INCOMPLETE`).
2. Any prohibited content from section 3.3 is present.
3. Any path violates section 3.2 (traversal, absolute, UNC, alternate data
   stream, or reparse indirection).
4. The candidate engine is multi-file.

### 11.2 Candidate identity

5. The candidate SHA-256 equals the current protected engine SHA-256.
6. The candidate bytes in the bundle do not match the manifest entry `sha256`.
7. The candidate `version` does not parse as a version.

### 11.3 Manifest

8. `schemaVersion` is not `2`.
9. Any unknown top-level or entry field is present.
10. Any REQUIRED field is missing or type-mismatched.
11. `status` is not `"approved"` for the candidate entry.
12. `sha256` is not 64 hex characters.
13. `downloadUrl` or `releaseNotesUrl` is not `https://`.
14. `capabilities` is absent, is not an array, contains an unrecognized token,
    contains a duplicate, contains a non-string or empty element, or exceeds the
    capability limit.
15. The capability is claimed in a schema v1 manifest.
16. The candidate entry appears more than once.
17. The version range excludes the shipping PAX Cookbook version, or any version
    string fails to parse (Finding A).

### 11.4 Signature and trust

18. Either envelope has an unknown field, a missing field, or
    `schemaVersion` other than `1`.
19. `hashAlgorithm` is not exactly `"SHA256"`.
20. `signatureAlgorithm` is not exactly `"RSA-PKCS1v15-SHA256"`.
21. `signerCertThumbprint` is not 40 hex characters, or does not self-match the
    embedded certificate.
22. `packageSha256` is not 64 hex characters, or does not match the recomputed
    package hash.
23. RSA signature verification fails.
24. The verified signer thumbprint does not match the pinned trust anchor
    (Finding D).
25. The two envelopes are signed by different signers.
26. `signerCertThumbprint` is used, mapped, or implied as an
    organization-certificate selector value.

### 11.5 Conformance evidence

27. The evidence index violates its closed schema.
28. `capability` is not exactly `organization_certificate_sha256_selector_v1`.
29. Any REQUIRED case ID is missing or duplicated.
30. Any case `result` is not `PASS`, or any `exitCode` is not `0`.
31. `failedCaseCount` is not `0`, or the summary counts disagree with `cases`.
32. Any raw evidence file is unindexed, missing, or hash-mismatched.
33. Any raw evidence header omits the REQUIRED bounded fields, or its recomputed
    candidate SHA-256 disagrees with the manifest entry.
34. Any `authenticationCallbackCount` is nonzero for any case.
35. Any machine-absolute path, build-agent identity, or sensitive value appears
    in the evidence.
36. A real organization certificate or private key was used to produce the
    fixtures.

### 11.6 Consistency

37. Any link in the eight-link binding chain is broken.
38. Static inspection contradicts an attested case outcome.
39. The bundle contradicts the behavior contract on any numbered requirement.
40. The bundle asserts a PAX Cookbook production capability that does not exist.

---

## 12. Explicit non-grants

An accepted candidate bundle grants **nothing** beyond eligibility for a later,
separately authorized evaluation step. Specifically, acceptance:

1. Does **NOT** update the `VERSION.json` `paxScript.sha256` pin (Finding B).
2. Does **NOT** perform, schedule, or authorize an acquisition, download, or
   install.
3. Does **NOT** activate, publish, or ship the candidate engine.
4. Does **NOT** replace, modify, or retire the current protected engine bytes.
5. Does **NOT** authorize a Bake, a Cook, or any PAX execution.
6. Does **NOT** authorize a live acquisition test; only a later, separately
   authorized **non-live** test is contemplated.
7. Does **NOT** assert tenant acceptance, entitlement, licensing, or customer
   readiness.
8. Does **NOT** verify authentication or prove the engine authenticates
   successfully anywhere.
9. Does **NOT** grant PAX Cookbook any certificate-store access; PAX Cookbook
   opens, reads, and mutates no certificate store at intake (Finding C).
10. Does **NOT** create, hold, or use any private key on the PAX Cookbook side.
11. Does **NOT** change the behavior contract, whose eleven numbered
    requirements remain authoritative and unedited.
12. Does **NOT** establish an organization-certificate SHA-1 selector bridge of
    any kind (section 6.5).
13. Does **NOT** guarantee upstream honesty; it establishes binding and
    accountability only (section 9.3).
14. Does **NOT** map to any Dev-Trio machine status (section 10.1).

---

## 13. Upstream producer checklist

Work top to bottom. Every line is REQUIRED.

**Candidate engine**

- [ ] Implements behavior-contract requirements 1 through 11 exactly.
- [ ] Single file, final published bytes, parseable version.
- [ ] SHA-256 differs from `99AB97232C76022771197B84AF880ED48D9E4B83F678F05262C38A223D3814C9`.

**Manifest**

- [ ] `schemaVersion` is `2`; the six top-level fields and nothing else.
- [ ] Candidate entry present exactly once, `status` is `"approved"`.
- [ ] `sha256` is 64 uppercase hex and equals the candidate bytes.
- [ ] `downloadUrl` is HTTPS and serves the exact candidate bytes.
- [ ] `capabilities` is exactly `["organization_certificate_sha256_selector_v1"]`.
- [ ] Version range admits the shipping PAX Cookbook version, confirmed out of
      band (Finding A).

**Signatures**

- [ ] Both `.sig` files carry the exact nine-field envelope.
- [ ] `hashAlgorithm` is `"SHA256"`; `signatureAlgorithm` is
      `"RSA-PKCS1v15-SHA256"`.
- [ ] Both are signed by the certificate PAX Cookbook has pinned (Finding D).
- [ ] `signerCertThumbprint` self-matches and is used only as signer identity.
- [ ] No private key material anywhere in the bundle.

**Conformance**

- [ ] Harness archive included and hashed in the evidence index.
- [ ] All thirteen REQUIRED cases executed against the exact candidate bytes.
- [ ] Fixtures produced with ephemeral/isolated stores on producer-owned agents,
      never with real organization certificates (Finding C).
- [ ] Every case `PASS`, every `exitCode` `0`, `failedCaseCount` `0`.
- [ ] One raw evidence file per case, indexed, hashed, and beginning at byte
      zero with the canonical seven-field compact JSON header of section 7.5,
      terminated by a single LF and containing no whitespace.
- [ ] `authenticationCallbackCount` is `0` for **every** one of the thirteen
      cases, measured at case exit.
- [ ] Exactly the thirteen required cases, with no supplementary case and no
      extra raw evidence file (section 8.2).

**Hygiene**

- [ ] No tenant, client, account, certificate, key, token, or secret values.
- [ ] No machine-absolute paths, drive letters, UNC paths, or agent names.
- [ ] All recorded paths bundle-root-relative with forward slashes and no
      traversal.
- [ ] No extra executable, installer, or binary.

**Expectations**

- [ ] Understood that acceptance is offline and non-live.
- [ ] Understood that PAX Cookbook must separately update the `VERSION.json`
      `paxScript.sha256` pin before any acquisition can succeed, in a separately
      authorized cycle that intake neither performs nor authorizes (Finding B).
- [ ] Understood that only the manifest envelope is verified in production
      today; the evidence envelope is an intake-time requirement for a future
      validator (section 6.6).
- [ ] Understood that acceptance grants none of the items in section 12.
