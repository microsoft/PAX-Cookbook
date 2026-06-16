# PAX Cookbook — Release Package Reference

This document describes the structure, content, and integrity model of a PAX Cookbook release. It is intended for the **distributor** who **builds** or **redistributes** a release, not the **chef** who only consumes one. End users (chefs) should start with [OPERATOR_GUIDE.md](OPERATOR_GUIDE.md) (the Chef Guide; filename retained for cross-link stability).

> **For coding agents (Claude / Copilot / future automated sessions):** before rebuilding, packaging, installing, updating, or repairing PAX Cookbook, read [CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md](CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md) end-to-end in the current session. That file is the doctrinal scaffold for these tasks; this document is the distributor reference behind it.

> *Throughout this reference the **distributor** is the role that runs `tools/release/Build-Release.ps1`, holds any release-signing key, and publishes the manifest URL. The **chef** is the role that runs the appliance on a Windows laptop, picks a workspace, edits recipes, and manages the local trust allowlist. In a self-distribution deployment (one person builds and runs their own release), the same human plays both roles — but the two responsibilities remain distinct in this document.*

Every statement in this document corresponds to behavior in `tools/release/Build-Release.ps1` and `tools/release/Release.psm1`. If you find a mismatch, the document is wrong.

---

## 1. What a release is

A release of PAX Cookbook is **five files** in one output directory:

```
<output>\
├── pax-cookbook-<version>.zip                  # The appliance ZIP (structurally deterministic; see §11)
├── pax-cookbook-<version>.zip.sha256           # SHA-256 attestation sidecar
├── pax-cookbook-<version>.release.json         # Build metadata (rich)
├── pax-cookbook-<version>.manifest.json        # Update-channel manifest snapshot
└── pax-cookbook-<version>.release-notes.md     # Chef-facing release notes (authored by the distributor)
```

There is no installer EXE / MSI / MSIX. The PowerShell installer is inside the ZIP at `app/install/Install-PAXCookbook.ps1`, with a top-level double-click wrapper `Install PAX Cookbook.cmd` that invokes it. There is no signed CAB. There is no Authenticode envelope around the ZIP itself.

If those statements ever stop being true, the change must be reflected here.

---

## 2. How the release is built

The build is driven by `tools/release/Build-Release.ps1`. The script takes the following parameters:

| Parameter | Purpose |
| --- | --- |
| `-RepoRoot` | Source tree to package. Defaults to the repo containing the script. |
| `-OutputRoot` | Directory to write the five artifacts into. Defaults to `dist\<channel>\pax-cookbook-<version>\`. |
| `-Channel` | Release channel string. Defaults to `VERSION.json -> channel` (currently `"stable"`). |
| `-SourceEpoch` | UTC epoch for deterministic ZIP entry timestamps. Defaults to `VERSION.json -> releaseTimestamp`, or `2024-01-01T00:00:00Z` if absent. |
| `-SourceCommit` | Optional VCS commit hash to stamp into `release.json`. |
| `-PackageBaseUrl` | Optional public base URL stamped into the manifest snapshot. |
| `-ReleaseNotesUrl` | Optional public URL stamped into the manifest snapshot. |
| `-BuildId` | Optional override. Defaults to a deterministic UUID derived from `version + channel + epoch`. |
| `-Force` | Permit overwriting an existing `-OutputRoot`. |

The build script is intentionally **single-pass and non-interactive**. It does not invoke `git`, does not invoke `winget`, does not invoke `dotnet`, does not invoke a signing tool, does not call out to the network.

---

## 3. Determinism contract

Two release builds run from **identical inputs on the same host with the same toolchain** produce a release whose **structure** is deterministic: the same five filenames in `OutputRoot`, the same `buildId`, the same `release.json` content for fields that derive from `SourceEpoch`, and the same SHA-256 sidecar contents for any artifact whose bytes have not changed. On the same machine, the ZIP itself typically hashes to the same SHA-256 across runs — but we do **not** promise cross-host bit-for-bit cryptographic reproducibility, because ZIP compression libraries and OS file metadata can legitimately differ between hosts. The contract is **structural determinism**, verified by the Phase AA harness; same-byte ZIP output is an emergent property of running both builds on the same host.

| Input | Source |
| --- | --- |
| Source tree | `-RepoRoot` |
| Source-epoch UTC | `-SourceEpoch` (or `VERSION.json -> releaseTimestamp`) |
| `VERSION.json` contents | Pinned in the source tree |
| Channel name | `-Channel` (or `VERSION.json -> channel`) |
| BuildId | `-BuildId` (or deterministically derived from the three above) |

How determinism is achieved:

- ZIP entry timestamps are forced to `-SourceEpoch`.
- ZIP entries are added in a sorted, normalized order (see `Get-ReleaseFileSet`).
- JSON output is written via a fixed-depth `ConvertTo-Json` followed by `\n`-only line endings and UTF-8 **without** BOM.
- Release notes go through the same `\n`-only / BOM-stripped writer.
- `BuiltAtUtc` defaults to `SourceEpoch` (no `Get-Date` leakage).

If you tamper with any input — including the system clock — your output will differ. That is the point.

---

## 4. The ZIP — `pax-cookbook-<version>.zip`

### 4.1 Contents

The ZIP is the **complete runtime appliance**. After extraction you get the same tree the installer copies into `%LOCALAPPDATA%\PAXCookbook\App\`, plus the launcher subtree, plus a top-level double-click installer wrapper. The PowerShell installer itself lives under `app\install\`, not at the package top level. Concretely (abbreviated):

```
<extracted>\
├── Install PAX Cookbook.cmd
├── launcher\
│   └── Start-PAXCookbook.ps1
└── app\
    ├── VERSION.json
    ├── install\
    │   └── Install-PAXCookbook.ps1
    ├── broker\
    ├── lib\
    ├── resources\
    │   └── pax\
    │       └── PAX_Purview_Audit_Log_Processor.ps1
    ├── templates\
    └── web\
```

The top-level `Install PAX Cookbook.cmd` is a thin convenience wrapper for double-click installation. It resolves its own directory, locates `pwsh.exe` (refusing to fall back to Windows PowerShell 5.1), and invokes `app\install\Install-PAXCookbook.ps1 install` with safe quoting for paths that contain spaces. The CMD wrapper does **not** elevate, does **not** bypass any installer integrity check, does **not** copy files itself, does **not** duplicate installer logic, does **not** change machine-wide PowerShell execution policy, does **not** modify PATH or any persistent environment variable, does **not** install or modify any Windows service, and does **not** modify any Windows Firewall rule. The PowerShell installer at `app\install\Install-PAXCookbook.ps1` remains the authoritative installer; the CMD wrapper exists purely as a double-click entry point.

### 4.2 What is **not** in the ZIP

The release walker (`Get-ReleaseFileSet` and `Test-ReleaseExclusion`) refuses to include:

- `.git\`, `.github\`, `.vscode\`, `node_modules\` — VCS / IDE / tool noise.
- `_temp\`, `_backup\`, `temp\` — internal verification and planning artifacts.
- `dist\` — prior release outputs.
- `*.log`, `*.tmp`, `*.bak` — transient build artifacts.
- Anything containing credentials, tokens, or keys (the exclusion list is defense-in-depth and is re-tested after the walker).

The build verifies the assertion by re-reading the resulting ZIP and re-running the exclusion test against every entry. If any excluded path leaks in, the build aborts.

**Cookbook v1 note.** For both the internal-test and production artifacts, `VERSION.json.paxScript.acquisitionPolicy = "external"` and the PAX script `app/resources/pax/PAX_Purview_Audit_Log_Processor.ps1` is **not** present in the ZIP. The script is acquired by the broker at first launch through an approved-engine manifest. See §11c for the single-flavor / signing-only-delta contract. The Phase 12 embedded artifact (which did ship the PAX script bytes inside the ZIP) is preserved as historical acceptance evidence only.

**The v1 release ZIP MUST NOT contain a modified, wrapped, normalized, re-encoded, signed-in-place, or Cookbook-customized PAX script** — not in `app/resources/pax/`, not under any other path, not under any other filename, not as a derived form, not as a fragment, and not as a placeholder or stub. The Cookbook side of the v1 wire is acquisition + validation + unchanged-bytes copy, not transformation. The release pipeline does not generate a Cookbook-blessed PAX script, does not vendor a transformed PAX script, does not bundle a per-tenant or per-recipe specialization of the script, and does not produce "Cookbook-customized" engine variants. The PAX script bytes that ever land on a Cookbook install come from the PAX team's approved-engine release through the broker-side acquisition pipeline, byte-for-byte unchanged, per the immutability contract codified in [`_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md` §4.5](../_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md#45-pax-script-immutability-contract) and [`docs/CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md` constitution rule 8](CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md#2-constitution-quick-reference). A build that produces an artifact carrying any PAX script bytes — modified or otherwise — under the external policy is a doctrine violation and the release is rejected.

### 4.3 Vendored vs immutable vs chef-owned

The files inside the ZIP fall into three classes. The classification matters because the **integrity model** differs for each.

| Class | What it is | Examples | Integrity model |
| --- | --- | --- | --- |
| **Vendored** | Third-party assets shipped verbatim inside the appliance. | `app/lib/Microsoft.Data.Sqlite.dll`, `app/lib/SQLitePCLRaw.*.dll`, `app/web/vendor/xterm/*` | SHA-256 pinned in the broker's integrity registry. Broker refuses to start on mismatch (exit code 5 for the SQLite DLL). |
| **Immutable bundled** | First-party assets that ship as part of the appliance and must be byte-identical to what was packaged. | `app/resources/pax/PAX_Purview_Audit_Log_Processor.ps1` (and its name + version + relative path) | SHA-256 pinned in `app/VERSION.json -> paxScript`. Broker refuses to start on mismatch (exit code 6). |
| **Chef-owned** | Files the chef can legitimately edit or create after install. | `<Workspace>\Recipes\*.recipe.json`, `<Workspace>\Trust\trusted-signers.json`, `%APPDATA%\PAXCookbook\cookbook.bootstrap.json` | No integrity check. The chef is the source of truth. |

The release package contains only **vendored** and **immutable bundled** files. There are no chef-owned files in the ZIP, and the installer never places chef-owned files. Recipes, trust allowlists, and bootstrap pointers come into existence only after the chef interacts with the appliance.

---

## 5. The SHA-256 sidecar — `pax-cookbook-<version>.zip.sha256`

A single-line text file in **sha256sum format**: the upper-case hex SHA-256 of the ZIP, followed by two spaces, followed by the ZIP's basename, followed by `\n`. Written UTF-8 without BOM by `Write-Sha256Sidecar`.

Example (for the V1.0.0 release):

```
3526AA088DD6BFE4E2276889E82DE97E59E672B4F29D49CC08CB2B536FABC38A  pax-cookbook-1.0.0.zip
```

This sidecar is the **primary integrity attestation** for the release. Both the broker (during update download) and any informed chef (during manual install) should verify it before trusting the ZIP.

Verification with the GNU `sha256sum` tool is a one-liner:

```bash
sha256sum -c pax-cookbook-<version>.zip.sha256
```

Verification in PowerShell extracts the leading hex token before comparing:

```powershell
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath 'pax-cookbook-<version>.zip').Hash
$expected = ((Get-Content -LiteralPath 'pax-cookbook-<version>.zip.sha256') -split '\s+', 2)[0]
$actual -eq $expected
```

---

## 6. The release metadata — `pax-cookbook-<version>.release.json`

Rich, single-build, deterministic metadata. The schema is produced by `New-ReleaseMetadata`. Fields are emitted **flat** at the top level (no nested `package` or `cookbook` objects); the important ones are:

| Field | Source |
| --- | --- |
| `schemaVersion` | Internal constant (currently `1`). |
| `cookbookVersion` | `VERSION.json -> cookbook.version` |
| `channel` | `-Channel` (default `"stable"`) |
| `buildId` | Deterministic UUID derived from version + channel + epoch, unless overridden. |
| `builtAtUtc` | `-SourceEpoch` (deterministic by default) |
| `builtOnHost` | `[Environment]::MachineName` — informational only |
| `sourceCommit` | Optional, from `-SourceCommit` |
| `packageFile`, `packageSizeBytes`, `packageSha256` | The ZIP, computed in-process |
| `manifestSchemaVersion` | Internal constant (currently `1`) used by the broker to gate accepted manifests. |
| `paxScript.name`, `paxScript.version`, `paxScript.relativePath`, `paxScript.sha256` | `VERSION.json -> paxScript`, surfaced as a nested object. |
| `fileCount` | Number of ZIP entries (immutable after build). |
| `exclusionPatternCount` | Number of exclusion rules the walker applied; cross-checked by the release-cut smoke. |
| `signing.state` | **`"unsigned"`** for any locally-built release. |
| `signing.verified` | **`false`** for any locally-built release. |
| `signing.signerCertThumbprint`, `signing.signedAtUtc`, `signing.signatureAlgorithm` | All `null` for an unsigned release. Populated only when the package is signed downstream. |
| `signing.sidecarFile` | The basename of the `.sha256` sidecar. |
| `signing.notes` | Constant string declaring the release is unsigned and pointing at the `.sha256` sidecar. |
| `notes` | Top-level honesty string declaring the release is unsigned. |

Note the **honesty** of the `signing.*` block. The release pipeline does not have a signing key. It does not produce one. It does not assert a signature it cannot make. If you sign your own release downstream, you replace these fields and add `.sig` / `.signer.json` artifacts yourself — see §10.

---

## 7. The manifest snapshot — `pax-cookbook-<version>.manifest.json`

This is the **wire format** the broker fetches from `updateManifestUrl`. A snapshot of it is included in the release directory so distributors can publish it verbatim alongside the ZIP.

The schema is produced by `New-ReleaseManifest`. The important fields are:

| Field | Purpose |
| --- | --- |
| `schemaVersion` | Internal constant (currently `1`). |
| `channel` | The release channel this manifest serves. |
| `releaseTimestamp` | UTC instant the release was built, in `yyyy-MM-ddTHH:mm:ssZ` form (matches `release.json -> builtAtUtc`). |
| `latestCookbook.version` | The cookbook version this manifest advertises. |
| `latestCookbook.packageUrl` | `<PackageBaseUrl>/<PackageFile>` when `-PackageBaseUrl` was provided; otherwise the placeholder string `<TODO_RELEASE_URL_PACKAGE_ZIP>` (the broker's `Test-UpdateManifestUrl` rejects placeholder strings, so the distributor must overwrite this before publishing for shared update-flow distribution). The broker accepts only `https://` and loopback `http://` URLs. |
| `latestCookbook.sha256` | What the broker should recompute and compare against after download. |
| `latestCookbook.releaseNotesUrl` | `<ReleaseNotesUrl>` when provided, otherwise the placeholder string `<TODO_RELEASE_NOTES_URL>`. |
| `includedPaxScript.name`, `.version`, `.relativePath`, `.sha256` | Mirrors `VERSION.json -> paxScript` so consumers can pre-validate compatibility. |
| `compatibility.minCookbookVersionForPaxScript` | Defaults to the current cookbook version. |
| `compatibility.minimumCompatibleInstallerVersion` | Defaults to the current cookbook version. |

The manifest snapshot does **not** carry a `signing` block. Signing state is reported only via `release.json -> signing` (producer intent) and the broker's runtime verification report (consumer-side `verified`). The broker accepts the manifest only if its `schemaVersion` is recognized; it then downloads the ZIP, recomputes SHA-256, compares against `latestCookbook.sha256`, and refuses to stage on mismatch.

---

## 8. The release notes — `pax-cookbook-<version>.release-notes.md`

Plain Markdown produced from `tools/release/RELEASE_NOTES.md.template` with the following placeholders substituted:

| Placeholder | Substitution |
| --- | --- |
| `{{COOKBOOK_VERSION}}` | `versionInfo.CookbookVersion` |
| `{{CHANNEL}}` | resolved channel |
| `{{PAX_SCRIPT_VERSION}}` | `versionInfo.PaxScriptVersion` |
| `{{PACKAGE_FILE}}` | The ZIP filename |
| `{{PACKAGE_SHA256}}` | Upper-case hex SHA-256 of the ZIP |
| `{{BUILT_AT_UTC}}` | `builtAtUtc.ToString('o')` |

The template lives in source control and reads honestly: it states that the release carries SHA-256 attestation only, that no cryptographic signature has been applied unless the distributor has separately signed it, and that `signing.state = "unsigned"`. Distributors are expected to extend the notes Markdown rather than overwrite the honesty.

---

## 9. The installer's role

Once a consumer has the ZIP, the installer takes over. The installer is `app/install/Install-PAXCookbook.ps1` **inside** the ZIP — there is no separate installer download. Double-clicking the top-level `Install PAX Cookbook.cmd` invokes the same PowerShell installer with `install` mode.

The installer has seven modes:

| Mode | What it does |
| --- | --- |
| `install` | First-time setup. Copies the appliance into `%LOCALAPPDATA%\PAXCookbook\App\`, creates the per-user Start Menu group `PAX Cookbook\` containing `PAX Cookbook`, `PAX Cookbook Support Mode`, `Repair PAX Cookbook Shortcuts`, `Stop PAX Cookbook Server`, and `Uninstall PAX Cookbook`. Utility shortcuts are tagged with `PKEY_AppUserModel_ExcludeFromShowInNewInstall` so Windows keeps them out of "Recently added"; the main `PAX Cookbook.lnk` is intentionally not tagged. Default also creates a Desktop shortcut (`-NoDesktopShortcut` opts out). |
| `update` | Re-deploy a newer ZIP. Renames the current `App\` into `Backups\App-<timestamp>\`, copies the new tree into `App\`, refreshes the shortcut set. |
| `uninstall` | Removes `App\` and shortcuts. With `-Purge`, removes everything under `%LOCALAPPDATA%\PAXCookbook\`. Never removes the workspace or any external export folder. Requires `-Yes`. |
| `uninstall-prompt` | User-facing entry point invoked by the **Uninstall PAX Cookbook** Start Menu shortcut. Shows a WinForms confirmation dialog, refuses while the broker is up, then relocates the installer to `%TEMP%\PAXCookbookUninstall-<UTC>-<8-hex>\` and re-launches it with `uninstall -Yes -Purge`. The `%TEMP%` folder is the one acceptable leftover artifact. |
| `check` | Read-only diagnostic. Reports presence, version, integrity-hash status. |
| `repair-shortcuts` | Recreate the canonical Start Menu group and (when not opted out) the Desktop shortcut against the current install. |
| `remove-shortcuts` | Internal CLI maintenance mode that deletes all PAX-owned user-scope shortcuts (Start Menu group + Desktop). Not exposed as a Start Menu entry. |

The installer:

- never elevates,
- never edits the registry,
- never installs a service or scheduled task,
- never mutates PATH or any other environment variable,
- never alters firewall / Defender / SmartScreen / Authenticode trust stores,
- never invokes a package manager,
- never fetches from the network — the ZIP must already be extracted next to it.

These guarantees are enforced statically by `_temp/phase_w_verification/verify_phase_w.ps1`.

---

## 10. Optional release signing (distributor or chef)

The release pipeline does **not** ship a signing key. If a distributor (or, in the self-distribution case, the chef themselves) wants to add a cryptographic signature on top of the SHA-256 attestation, the supported flow is:

1. Produce a detached signature over the ZIP using **RSA-PKCS1v15-SHA256**.
2. Wrap it in the envelope schema understood by `app/broker/Update/Signature.psm1` (`schemaVersion: 1`).
3. Write the envelope as `<package>.zip.sig`.
4. Write the signer metadata as `<package>.zip.signer.json` (subject DN, issuer DN, SHA-1 thumbprint, validity window).
5. Add the SHA-1 thumbprint to every consumer's `<Workspace>\Trust\trusted-signers.json` allowlist. *(For self-distribution, the same person who signed the package adds their own thumbprint to their own workspace allowlist.)*
6. Re-emit `release.json -> signing` and `manifest.json -> signing` with `state = "signed"` and the relevant identity. (Note: the broker reports back `verified`, not `signed`, after it has actually run the verifier; `signed` only describes producer intent. See [OPERATOR_GUIDE.md §10](OPERATOR_GUIDE.md#10-trust--signature-lifecycle) for the consumer-side state table.)

There is no GPL, no PGP, no Authenticode pathway in the broker today. Only RSA-PKCS1v15-SHA256 in the envelope schema documented in `Signature.psm1`. If you need an additional algorithm, that is a code change, not a configuration change.

---

## 11. Verifying a release before publishing

The recommended pre-publish checklist:

1. Build the release with `tools/release/Build-Release.ps1`.
2. Confirm the five expected files exist in `OutputRoot`.
3. Confirm the ZIP's recomputed SHA-256 matches the `.sha256` sidecar.
4. Confirm `release.json -> package.sha256` matches the same value.
5. Confirm `manifest.json -> package.sha256` matches the same value.
6. Confirm `release.json -> paxScript.sha256` matches `app/VERSION.json -> paxScript.sha256` inside the ZIP.
7. (Optional) Run a second build from the same inputs and confirm **structural** equality: the same five filenames in `OutputRoot`, the same `buildId`, and identical `release.json` content for the fields that derive from `SourceEpoch`. The pipeline pins ZIP entry timestamps from `SourceEpoch` and writes entries in sorted order, which makes the structure deterministic. We do **not** claim cross-host bit-for-bit cryptographic reproducibility: ZIP compression, OS-level metadata, and runtime libraries can legitimately differ between hosts. If you need the two ZIPs to also hash to the same SHA-256, run both builds on the same machine with the same toolchain and the same `SourceEpoch`, and confirm by comparing SHA-256 sidecars.

A built-in script in `tools/release/` may exist to automate this. If it doesn't, the steps above are intentionally short enough to run by hand.

---

## 11a. Bundled workflow runtime behavior

This section is for enterprise reviewers and chefs evaluating Cookbook for deployment in policy-restricted environments. It calls out runtime behavior that originates from the bundled PAX script — not from Cookbook itself — so that policy compatibility can be assessed honestly before deployment.

**Cookbook itself does not directly invoke package managers as part of its own installation or runtime lifecycle.** §9 above lists what the installer never does, and `_temp/phase_w_verification/verify_phase_w.ps1` enforces those guarantees statically against the installer, launcher, broker, and the SPA the broker serves.

However, certain bundled recipes — specifically those built from the **M365 Usage Analytics Dashboard** and **AI-in-One Dashboard** templates, or any recipe whose `processing.rollup` is set to `"Rollup"` or `"RollupPlusRaw"` — invoke PAX in rollup mode. PAX's rollup post-processor is implemented in Python (3.10 or newer). The bundled PAX script (`app/resources/pax/PAX_Purview_Audit_Log_Processor.ps1`) owns the Python lifecycle:

- If a compatible Python interpreter is already on `PATH`, PAX uses it and takes no further bootstrap action.
- If no compatible interpreter is found, PAX itself attempts a per-user silent install via `winget Python.Python.3.13`.
- If `winget` is unavailable or blocked, PAX falls back to downloading and running the official python.org installer in per-user mode.

These bootstrap actions are taken by the bundled PAX script inside the bake's process tree when a chef runs a rollup-based recipe. Cookbook orchestrates the PAX invocation; it does not initiate, mediate, or override PAX's bootstrap decisions, and it does not vendor, validate, or detect Python on PAX's behalf.

Enterprise environments may restrict any or all of the following:

- `winget` invocation by non-admin users
- per-user (`MSI`-less) software installs
- outbound downloads from python.org
- arbitrary Python interpreter installation by end users

**Chefs should validate policy compatibility before running rollup-based recipes in locked-down environments.** Two practical paths exist:

1. **Pre-provision Python.** Make a Python 3.10+ interpreter available on `PATH` for the target users before they run a rollup recipe. PAX's `Resolve-PythonExe` will discover it and skip its own bootstrap entirely.
2. **Avoid rollup recipes.** Recipes whose `processing.rollup` is unset or `"None"` do not invoke PAX's Python code path and have no runtime dependency beyond PowerShell 7.4+.

This boundary is doctrinal. Cookbook does not own — and will not grow — a Python validator, a runtime-readiness dashboard, an environment health scanner, a dependency-management surface, or any other "auto-heal" framework. Python is PAX's responsibility. Policy assessment is the chef's responsibility. See [docs/OPERATOR_GUIDE.md §3.1](OPERATOR_GUIDE.md#31-prerequisites) for the chef-facing prerequisite chart and [docs/TROUBLESHOOTING.md](TROUBLESHOOTING.md) §11a for triage when a rollup bake reports Python unavailable.

---

## 11b. Recipe Takeout v1 file set

Recipe Takeout v1 ships in this Cookbook release as a recipe-definition transport feature. It is a Cookbook-side feature — the bundled PAX script is not modified by Recipe Takeout, and Recipe Takeout never invokes PAX. This section names the file set and the verification a distributor should run before publishing a release that includes Recipe Takeout.

### 11b.1 Recipe Takeout production file set

All files below ship inside the release ZIP under their listed paths. They are first-party Cookbook code and follow the same "Immutable bundled" classification used for the rest of `App\` in §4.3.

| File | Role |
| --- | --- |
| `app\broker\Routes\RecipeTakeout.ps1` | Broker route handlers for the validate, export, and import endpoints. |
| `app\broker\Modules\RecipeTakeoutSanitizer.psm1` | Export-side allow-list sanitizer and forbidden-field deny-list. Fails closed. |
| `app\broker\Modules\RecipeTakeoutImporter.psm1` | Import-side envelope validator, name-conflict resolver, and disk writer. |
| `app\web\assets\schemas\recipe-takeout.schema.json` | JSON schema for the `.paxrecipe.json` transport envelope. Consumed by both broker validate and the SPA preview card. |
| `app\web\assets\recipe-list.js` | Recipes page surface that hosts the **Import Recipe Takeout** wizard. |
| `app\web\assets\recipe-editor.js` | Prep Station surface that hosts the **Export Recipe Takeout** button. |
| `app\web\assets\help-panel.js` | Contextual help surface that hosts the `recipes.takeout` topic. |

The chef-facing documentation surfaces that name Recipe Takeout are:

| Doc | Section |
| --- | --- |
| `docs\OPERATOR_GUIDE.md` | §6a (Recipe Takeout) |
| `docs\TROUBLESHOOTING.md` | §17a (Recipe Takeout errors and surprises) |
| `docs\RELEASE_PACKAGE.md` | §11b (this section) |
| `README.md` | "Recipe-Based Workflows" |

### 11b.2 Pre-publish verification specific to Recipe Takeout

In addition to the general pre-publish checklist in §11, a release that includes Recipe Takeout should pass the following before publishing:

1. **Schema is well-formed.** `app\web\assets\schemas\recipe-takeout.schema.json` parses as JSON and validates against the JSON Schema 2020-12 meta-schema. The broker dot-sources this file on startup; a malformed schema would prevent the broker from booting.
2. **Sanitizer and importer modules import cleanly.** `Import-Module` on `RecipeTakeoutSanitizer.psm1` and `RecipeTakeoutImporter.psm1` returns without error and the expected public functions are exported.
3. **Broker route is wired.** `app\broker\Start-Broker.ps1` (or the route registration it calls) dot-sources `Routes\RecipeTakeout.ps1`. The endpoints exposed are listed in the OpenAPI surface bundled in the release.
4. **Doc/code shape consistency.** Every error code documented in `TROUBLESHOOTING.md §17a` exists as a literal string in `Routes\RecipeTakeout.ps1`, `RecipeTakeoutSanitizer.psm1`, or `RecipeTakeoutImporter.psm1`. No documented code is absent from code; no code in code is undocumented.
5. **Help topic is registered.** `app\web\assets\help-panel.js` contains the `recipes.takeout` topic id and the topic is reachable from the `recipes` category card on the Help home page.
6. **Forbidden doctrine is absent from shipped surfaces.** The shipped help topic and shipped docs do not contain `(Imported)` as a forced recipe-name pattern, do not document `takeout_not_confirmed` as an active error, do not document `takeout_name_unresolvable` as an active error, do not claim filename-based recipe-name resolution, and do not claim live dependency on the `.paxrecipe.json` file after import.

The smoke harness under `_temp\phase_uxr_f2h_recipe_takeout_help_docs_finalization_verification\` runs all six of the above as static text checks and is the canonical proof that this release ships a help/docs surface consistent with the Recipe Takeout v1 contract.

### 11b.3 Release-note language for Recipe Takeout

A release that introduces or modifies Recipe Takeout should use language consistent with the chef-facing contract:

- **Do** say: "Recipe Takeout moves a single recipe definition between Cookbook workspaces. Export from a saved recipe; import on the Recipes page."
- **Do** say: "Recipe Takeout does not move bakes, logs, output data, or credentials."
- **Don't** say: "Recipe Takeout backs up your workspace." (It doesn't.)
- **Don't** say: "Imported recipes are renamed `(Imported)` automatically." (They aren't; the chef chooses the name.)
- **Don't** say: "Recipe Takeout syncs recipes between machines." (It is a one-shot transport, not a sync.)

---

## 11c. External PAX engine acquisition (Cookbook v1)

This section codifies the Cookbook v1 release matrix defined by [`_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md`](../_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md) and supersedes the embedded-payload assumptions baked into §4.1 / §4.3 / §5.

### 11c.1 Single release flavor

There is **one** release architecture for v1. The internal-test and production release ZIPs are produced from the same source tree, by the same build pipeline, with the same exclusion rules, and with the same `VERSION.json.paxScript.acquisitionPolicy = "external"` value. The two artifacts differ **only** in:

1. **Signing / cert status.** Production is Authenticode-signed and/or carries a detached RSA-PKCS1v15-SHA256 envelope with a thumbprint chained to the production code-signing certificate. Internal-test may be unsigned or signed by a development cert.
2. **Distribution channel.** Internal-test goes to verifiers / smoke harnesses / Brian; production goes to the GitHub Release page (or equivalent customer-facing surface).
3. **Manifest-signature policy.** Production sets `VERSION.json.paxScript.manifestSignaturePolicy = "required"` with a populated `engineManifestTrustAnchorThumbprint` and a signed approved-engine manifest. An internal-test-unsigned build may set `manifestSignaturePolicy = "internal-test-bypass"` with `engineManifestTrustAnchorThumbprint = null`, which skips detached-signature verification **only** (see §11c.7).

Apart from the manifest-signature policy in item 3, the acquisition flow, the broker state machine, the SPA surface, the schema, the SHA-256 pin check, and the per-cook integrity model are **identical** between the two artifacts. Every check other than detached-signature verification runs the same way regardless of policy. A bug surfaced by internal-test acceptance is the same bug that would surface in production; that equivalence is the entire reason internal-test is a credible pre-release rehearsal.

### 11c.2 Absent from the v1 release ZIP

For both internal-test and production v1 artifacts, the release ZIP does **not** contain:

- `app/resources/pax/PAX_Purview_Audit_Log_Processor.ps1` (the PAX engine script bytes).

Every other file enumerated in §4.1 is unchanged.

### 11c.3 Present in the v1 release ZIP

The v1 release ZIP adds the following bytes (or non-zero values) over the Phase 12 embedded artifact:

- `VERSION.json.paxScript.acquisitionPolicy = "external"` (string).
- `VERSION.json.paxScript.engineManifestUrl` (HTTPS URL pointing at the PAX team's signed approved-engine manifest).
- `VERSION.json.paxScript.engineManifestTrustAnchorThumbprint` (SHA-1 hex thumbprint of the trust anchor the broker uses to verify the manifest signature).
- `VERSION.json.paxScript.exportEnabled` (boolean; defaults to `false` per D6).
- `VERSION.json.paxScript.manifestSignaturePolicy` (string; `"required"` for production, `"internal-test-bypass"` for an internal-test-unsigned build). Under `"internal-test-bypass"` the broker skips detached-signature verification **only**; every other acquisition check is unchanged.
- `VERSION.json.paxScript.sha256` and `VERSION.json.paxScript.version` remain the pinned **expected** values that the manifest entry MUST match (no drift permitted per D8).
- `app/resources/manifest.json.includedPaxScript.*` mirrors the same expected values.

The four new fields are validated at build time by the release pipeline's post-build check. The bundled-PAX SHA-256 post-copy check (§9 step in the installer) is **skipped** on external-policy artifacts because the script bytes are not present at install time; the broker re-runs `Test-BundledPaxIntegrity` against the canonical path after acquisition writes it.

### 11c.4 What the Phase 12 embedded artifact is now

The Phase 12 internal-test artifact (`PAXCookbookSetup.exe`, SHA-256 `8DD2CF81AF7986775F7F26E1E54DF8DFA545E8BDE2C47B10CA2F9C3D7C5FF975`) is preserved as **historical acceptance evidence only** for the embedded-payload era. It is **not** the active internal-test release candidate, **not** regenerated, **not** re-tested, **not** signed for distribution, and **not** part of the v1 release matrix. The validator carve-out that accepts `acquisitionPolicy = "embedded"` exists exclusively so that audit tooling can still read the historical artifact's metadata; the carve-out emits a `legacy_historical_artifact_detected` warning on every read.

### 11c.5 Forward-direction artifact filename (D5)

The current release tooling produces `pax-cookbook-<version>.zip` containing `Install PAX Cookbook.cmd`. The forward direction (per D5 in the plan) is a single `PAXCookbookSetup.exe` customer-facing artifact, but that transition is a **separate workstream** from external PAX acquisition and is **not** a precondition of the contract in this section. Either packaging shape — ZIP + `.cmd`, or a future self-extracting EXE — hosts the same external-acquisition flow described above. Internal-test vs production are never distinguished by filename or suffix; only by signing/cert status and distribution channel.

### 11c.6 PAX script immutability binds the release pipeline

The release pipeline is bound by the PAX script immutability contract in [`_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md` §4.5](../_temp/phase_13_external_pax_engine/EXTERNAL_PAX_ENGINE_ACQUISITION_PLAN.md#45-pax-script-immutability-contract) and constitution rule 8 in [`docs/CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md`](CLAUDE_RELEASE_AND_INSTALL_DOCTRINE.md#2-constitution-quick-reference). Concretely, for v1:

- The release walker does **not** read, copy, transform, normalize, re-encode, sign, wrap, or otherwise touch any PAX script bytes. There is no PAX script in the source tree to package, and the walker actively rejects (does not silently exclude) `app/resources/pax/PAX_Purview_Audit_Log_Processor.ps1` if a stray copy ever appears there — such a copy is a build environment contamination, not packaging input.
- The release pipeline does **not** generate a derived PAX script (no "Cookbook-customized" variant, no per-cookbook-version specialization of the engine, no preamble-injected copy, no wrapper module that re-exports modified functions).
- The release pipeline does **not** modify `VERSION.json.paxScript.sha256` to track a Cookbook-side rebuild of the script. The pin is whatever the PAX team published in the corresponding approved-engine manifest entry; if the pinned value and the manifest entry disagree, the release is rejected, never reconciled by mutating the pin.
- The release pipeline does **not** ship a Cookbook-side signed manifest of the PAX script. Signed approved-engine manifest authorship lives with the PAX team; Cookbook ships only the **URL** to that manifest and the **trust anchor thumbprint** required to verify its signature.
- The release pipeline's post-build check enforces both the exclusion (no script bytes present under the external policy) and the immutability scope (no derived / wrapped / normalized script anywhere in the ZIP). Either violation fails the build.

The Phase 12 historical artifact carve-out in §11c.4 does **not** waive any of the above: the historical artifact is read-only audit evidence of the embedded-payload era, never a precedent for shipping modified PAX script bytes under v1.

### 11c.7 The internal-test-unsigned artifact is non-publishable

An internal-test-unsigned build — the artifact produced with `VERSION.json.paxScript.manifestSignaturePolicy = "internal-test-bypass"` and `engineManifestTrustAnchorThumbprint = null` — is **for internal testing only and MUST NOT be published to customers**. It exists so verifiers, smoke harnesses, and internal acceptance can exercise the full acquisition pipeline against an approved-engine manifest that is not yet covered by the production signing chain.

- The `internal-test-bypass` policy relaxes detached-signature verification **only**. JSON-schema validation of the manifest, approved-entry selection, the SHA-256 pin check, and exact-byte activation at the canonical install path all run identically to a production build. The engine bytes that activate are still exactly the approved bytes.
- The build is **not** a customer-facing release candidate. It is not posted to the GitHub Release page or any equivalent customer surface, and it carries the **Internal-test build (not customer-facing)** banner in the acquisition dialog so an operator cannot mistake it for production.
- There is no "promote the internal-test ZIP to production" path. Production is a distinct artifact built with `manifestSignaturePolicy = "required"`, a real trust anchor, and a signed approved-engine manifest (see §11c.8). Re-labeling or re-signing the internal-test artifact is **not** a substitute for that.

### 11c.8 Production close — required inputs

Closing to a customer-facing production release requires **all** of the following inputs to be present and correct before the production artifact is built. None of these are supplied by an internal-test-unsigned build, and this section is **not** a license to perform that close now — it enumerates the prerequisites for a future production workstream.

1. **HTTPS `engineManifestUrl`.** `VERSION.json.paxScript.engineManifestUrl` MUST point at the PAX team's production approved-engine manifest over `https://` (not a loopback / `127.0.0.1` internal-test endpoint).
2. **Trust-anchor thumbprint.** `VERSION.json.paxScript.engineManifestTrustAnchorThumbprint` MUST be a populated 40-hex-character SHA-1 thumbprint of the trust anchor the broker uses to verify the manifest signature (not `null`).
3. **Signed approved-engine manifest.** The manifest the `engineManifestUrl` serves MUST carry a valid detached RSA-PKCS1v15-SHA256 signature chained to that trust anchor.
4. **`manifestSignaturePolicy = "required"`.** Both `VERSION.json.paxScript.manifestSignaturePolicy` and `app/resources/manifest.json.includedPaxScript.manifestSignaturePolicy` MUST be `"required"`, so the broker enforces detached-signature verification.
5. **Production code-signing certificate.** The release ZIP / installer MUST be Authenticode-signed (and/or carry the detached envelope) under the production code-signing certificate per §11c.1 item 1.

Until every item above is in place, the only buildable v1 artifact is the internal-test-unsigned build, which is non-publishable per §11c.7.

---

## 12. Where this reference stops

This reference does **not** cover:

- Recipe / bake semantics — see [OPERATOR_GUIDE.md](OPERATOR_GUIDE.md).
- Symptom-by-symptom triage — see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).
- The PAX script's own command-line surface — see the [PAX repository](https://github.com/Microsoft/PAX).
- Internal phase verification harnesses — those live under `_temp/`.

If you are about to distribute a Cookbook release and are unsure whether something you are doing is compatible with this reference, the safe answer is: do not do it. Open an issue first.
